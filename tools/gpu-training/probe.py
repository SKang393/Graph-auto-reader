# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Check the installed RX 6600 XT backend without datasets or result files."""
import json
import sys
import traceback


def main() -> int:
    report = {"python": sys.version, "executable": sys.executable}
    try:
        import torch

        report.update(torch=torch.__version__, hip=torch.version.hip,
                      gpu_available=torch.cuda.is_available())
        if not torch.version.hip or not torch.cuda.is_available():
            raise RuntimeError("ROCm GPU backend unavailable; CPU fallback is forbidden")
        devices = []
        for index in range(torch.cuda.device_count()):
            properties = torch.cuda.get_device_properties(index)
            devices.append({"index": index, "name": properties.name,
                            "memory_bytes": properties.total_memory,
                            "architecture": getattr(properties, "gcnArchName", None)})
        report["devices"] = devices
        matches = [item for item in devices if "RX 6600 XT" in item["name"]]
        if len(matches) != 1:
            raise RuntimeError("Expected one RX 6600 XT; CPU fallback is forbidden")
        device = torch.device("cuda", matches[0]["index"])
        torch.cuda.set_device(device)
        torch.use_deterministic_algorithms(True)
        tensor = torch.arange(256, dtype=torch.float32, device=device)
        tensor = tensor.reshape(16, 16).requires_grad_()
        loss = (tensor @ tensor.T).square().mean()
        loss.backward()
        convolution = torch.nn.Conv2d(3, 4, 3, padding=1, device=device)
        features = torch.randn(2, 3, 16, 16, device=device)
        convolution(features).square().mean().backward()
        torch.cuda.synchronize(device)
        if tensor.grad is None or not torch.isfinite(tensor.grad).all().item():
            raise RuntimeError("GPU backward produced missing or non-finite gradients")
        if not all(parameter.grad is not None
                   and parameter.grad.device == device
                   and torch.isfinite(parameter.grad).all().item()
                   for parameter in convolution.parameters()):
            raise RuntimeError("GPU convolution backward failed its gradient check")
        report.update(status="pass", tensor_device=str(tensor.device),
                      gradient_device=str(tensor.grad.device), loss=loss.item(),
                      finite_gradients=True, convolution_gpu_gradients=True,
                      cpu_fallback=False)
    except Exception:
        report.update(status="fail", error=traceback.format_exc())
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
