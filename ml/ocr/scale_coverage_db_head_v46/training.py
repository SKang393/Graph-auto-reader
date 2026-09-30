# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded head adaptation with train-only selection and epoch recovery."""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import time

import torch

from ml.ocr.official_bakeoff import frozen_head_training as base

RECIPE={'epochs':24,'seed':20260930,'learning_rate':0.00003,'weight_decay':0.0001,
    'batch_size':1,'intraop_threads':12,'checkpoint_selection':'earliest_minimum_full_train_loss',
    'loss':'inverse_unclip_masked_dice_with_empty_mean_probability',
    'frozen_batch_norm':True,'torch_backend':base.TORCH_BACKEND}

def atomic_json(path,value):
    temp=path.with_name(path.name+'.pending')
    temp.write_text(json.dumps(value,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    os.replace(temp,path)

def train(head,panels,recipe,output,budget,binding,*,resume=None):
    base._validate_head(head)
    samples=base._prepare_samples(panels)
    torch.set_num_threads(recipe['intraop_threads'])
    torch.backends.mkldnn.enabled=False
    torch.use_deterministic_algorithms(True)
    head.eval()
    frozen=base._frozen_bn_sha256(head)
    generator=torch.Generator(device='cpu').manual_seed(recipe['seed'])
    optimizer=torch.optim.AdamW(head.parameters(),lr=recipe['learning_rate'],
        weight_decay=recipe['weight_decay'],foreach=False)
    identity={'recipe':recipe,'input_binding':binding,
        'ordered_panel_ids':[s.panel_id for s in samples],'frozen_batch_norm_sha256':frozen}
    completed=steps=selected=0
    losses=[]
    best_loss=math.inf
    best_state=None
    started=time.perf_counter()
    if resume is not None:
        checkpoint=torch.load(resume,map_location='cpu',weights_only=True)
        if checkpoint['identity']!=identity:
            raise ValueError('Recovery input, recipe or frozen state changed')
        completed,steps=checkpoint['completed_epochs'],checkpoint['optimizer_steps']
        losses=checkpoint['epoch_losses']
        if (not 0<completed<=recipe['epochs'] or steps!=completed*len(samples) or
            len(losses)!=completed or any(r['epoch']!=i+1 for i,r in enumerate(losses))):
            raise ValueError('Recovery epoch accounting changed')
        head.load_state_dict(checkpoint['state_dict'],strict=True)
        base._validate_head(head)
        if base._frozen_bn_sha256(head)!=frozen:
            raise ValueError('Recovery changed frozen batch normalization')
        optimizer.load_state_dict(checkpoint['optimizer'])
        if any(g['lr']!=recipe['learning_rate'] or g['weight_decay']!=recipe['weight_decay']
            for g in optimizer.param_groups):
            raise ValueError('Recovery optimizer recipe changed')
        generator.set_state(checkpoint['draw_generator'])
        best_state,best_loss,selected=(checkpoint[k] for k in ('best_state','best_loss','selected_epoch'))
        if not math.isfinite(best_loss) or not 1<=selected<=completed:
            raise ValueError('Recovery train-only selection is invalid')
    for epoch in range(completed+1,recipe['epochs']+1):
        order=torch.randperm(len(samples),generator=generator).tolist()
        step_losses=[]
        for index in order:
            with budget.work_block():
                sample=samples[index]
                optimizer.zero_grad(set_to_none=True)
                loss,_=base.masked_db_dice_loss(head.forward_logits(sample.features),sample.target,sample.mask)
                base._require_finite_scalar(loss,'Training loss')
                loss.backward()
                base._validate_gradients(head)
                optimizer.step()
                base._validate_parameters(head)
                step_losses.append(float(loss.detach()))
                steps+=1
        full=[]
        with torch.inference_mode():
            for sample in samples:
                with budget.work_block():
                    loss,_=base.masked_db_dice_loss(head.forward_logits(sample.features),sample.target,sample.mask)
                    base._require_finite_scalar(loss,'Full-train loss')
                    full.append(float(loss))
        mean=sum(full)/len(full)
        if mean<best_loss:
            best_loss,selected=mean,epoch
            best_state=base._clone_state(head)
        losses.append({'epoch':epoch,'optimizer_steps':steps,
            'training_step_mean_loss':sum(step_losses)/len(step_losses),'full_train_loss':mean,
            'draw_order_sha256':base._ordered_ids_sha256([samples[i].panel_id for i in order])})
        checkpoint={'identity':identity,'completed_epochs':epoch,'optimizer_steps':steps,
            'epoch_losses':losses,'state_dict':base._clone_state(head),'optimizer':optimizer.state_dict(),
            'draw_generator':generator.get_state(),'best_state':best_state,'best_loss':best_loss,'selected_epoch':selected}
        temporary=output/'recovery.pending.pt'
        torch.save(checkpoint,temporary)
        os.replace(temporary,output/'recovery.pt')
        atomic_json(output/'progress.json',{'status':'training','epoch':epoch,
            'optimizer_steps':steps,'seconds':time.perf_counter()-started})
    if best_state is None:
        raise ValueError('No finite training checkpoint')
    head.load_state_dict(best_state,strict=True)
    head.eval()
    assert base._frozen_bn_sha256(head)==frozen
    return {'epochs':recipe['epochs'],'optimizer_steps':steps,'selected_epoch':selected,
        'epoch_losses':losses,'panel_count':len(samples),'recipe':recipe,
        'frozen_batch_norm_sha256_before':frozen,'frozen_batch_norm_sha256_after':base._frozen_bn_sha256(head),
        'selected_trainable_state_sha256':base._trainable_state_sha256(head),
        'resumed_after_epoch':completed,'seconds':time.perf_counter()-started}
