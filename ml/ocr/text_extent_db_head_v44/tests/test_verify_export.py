# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest
from ml.ocr.text_extent_db_head_v44 import verify_export as verifier


def _result():
    return {'optimizer_steps': 6720, 'epochs': 240, 'panel_count': 28,
            'positive_panel_count': 28, 'empty_positive_panels': 0,
            'intraop_threads': 12, 'seed': verifier.runner.RECIPE['seed'], 'batch_size': 1,
            'learning_rate': .0001, 'weight_decay': .0001, 'dice_epsilon': 1e-6,
            'torch_backend': verifier.runner.engine.TORCH_BACKEND,
            'trainable_parameter_names': list(verifier.runner.engine.TRAINABLE_PARAMETER_NAMES),
            'selected_epoch': 1, 'frozen_batch_norm_sha256_before': 'a'*64,
            'frozen_batch_norm_sha256_after': 'a'*64,
            'epoch_losses': [{'epoch': i, 'optimizer_steps': i*28, 'full_train_loss': .5,
                'training_step_mean_loss': .5, 'full_positive_dice_loss': .5,
                'full_empty_negative_loss': None, 'draw_order_sha256': 'b'*64} for i in range(1,241)]}


def test_export_requires_earliest_minimum_training_checkpoint():
    verifier.validate_training_result(_result())


@pytest.mark.parametrize('defect', ['threads','omission','epoch','nonfinite','partition','selection','normalization'])
def test_export_rejects_inconsistent_training_evidence(defect):
    result = _result()
    if defect == 'threads': result['intraop_threads'] = 16
    elif defect == 'omission': result['epoch_losses'].pop()
    elif defect == 'epoch': result['epoch_losses'][0]['epoch'] = True
    elif defect == 'nonfinite': result['epoch_losses'][0]['full_train_loss'] = float('nan')
    elif defect == 'partition': result['epoch_losses'][0]['full_positive_dice_loss'] = .4
    elif defect == 'selection': result['selected_epoch'] = 240
    else: result['frozen_batch_norm_sha256_after'] = 'c'*64
    with pytest.raises(ValueError):
        verifier.validate_training_result(result)
