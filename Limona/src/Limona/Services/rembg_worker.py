"""
Background removal script using U2-NetP ONNX model via OpenCV DNN.
Called by Limona C# app as: python rembg_worker.py <input_path> <output_path>

Uses OpenCV DNN backend (already installed) — no extra dependencies needed.
Model: U2-NetP (~4.5 MB ONNX) — fast, good quality, 100% local.
"""

import sys
import os
import numpy as np
import cv2

MODEL_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Limona", "models")
MODEL_PATH = os.path.join(MODEL_DIR, "u2netp.onnx")
INPUT_SIZE = 320  # U2NetP uses 320x320


def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-x))


def normalize(img: np.ndarray) -> np.ndarray:
    """Normalize to ImageNet mean/std, return NCHW float32 blob."""
    img = img.astype(np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std  = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    img = (img - mean) / std
    # HWC -> CHW -> NCHW
    img = np.transpose(img, (2, 0, 1))
    return np.expand_dims(img, axis=0).astype(np.float32)


def remove_background(input_path: str, output_path: str, mask_out: str | None = None) -> None:
    if not os.path.isfile(MODEL_PATH):
        raise FileNotFoundError(
            f"Model not found: {MODEL_PATH}\n"
            "Please download u2netp.onnx from:\n"
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx\n"
            f"and place it in: {MODEL_DIR}"
        )

    # Read source image
    bgr = cv2.imread(input_path, cv2.IMREAD_UNCHANGED)
    if bgr is None:
        # Try PIL for exotic formats (AVIF, HEIC…)
        from PIL import Image
        pil = Image.open(input_path).convert("RGB")
        bgr = cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)

    orig_h, orig_w = bgr.shape[:2]
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)

    # Resize for model input
    resized = cv2.resize(rgb, (INPUT_SIZE, INPUT_SIZE), interpolation=cv2.INTER_LINEAR)

    # Build blob and run inference
    blob = normalize(resized)
    net = cv2.dnn.readNetFromONNX(MODEL_PATH)
    net.setInput(blob)
    # U2NetP output: first output "1586" or "1580" is the full-res fused prediction
    output_names = net.getUnconnectedOutLayersNames()
    outputs = net.forward(output_names)

    # Some exports emit raw logits, some emit sigmoid-activated maps (0..1).
    # Applying sigmoid to values already in [0,1] squashes everything into
    # 0.5..0.73, which makes the WHOLE image semi-transparent. Detect and handle.
    pred = outputs[0][0, 0]     # (320, 320) float
    if pred.min() < 0.0 or pred.max() > 1.0:
        pred = sigmoid(pred)

    # Min-max normalize exactly like rembg does: restores full 0..1 contrast
    # even when the model outputs compressed values.
    ma, mi = float(pred.max()), float(pred.min())
    if ma - mi > 1e-6:
        pred = (pred - mi) / (ma - mi)
    pred = (pred * 255.0).astype(np.uint8)

    # Resize mask back to original resolution
    mask = cv2.resize(pred, (orig_w, orig_h), interpolation=cv2.INTER_LINEAR)

    # Slightly denoise the mask (the C# app composes with its own threshold +
    # feather, so keep the raw mask intact here — only a light blur)
    mask = cv2.GaussianBlur(mask, (0, 0), sigmaX=0.5)

    # Save the raw normalized mask if requested (used for live re-compositing)
    if mask_out:
        cv2.imwrite(mask_out, mask)

    # Compose BGRA output
    if bgr.shape[2] == 4:
        out = bgr.copy()
        out[:, :, 3] = mask
    else:
        out = cv2.cvtColor(bgr, cv2.COLOR_BGR2BGRA)
        out[:, :, 3] = mask

    cv2.imwrite(output_path, out)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python rembg_worker.py <input_path> <output_path> [mask_out_path]", file=sys.stderr)
        sys.exit(1)

    inp = sys.argv[1]
    out = sys.argv[2]
    mask_out = sys.argv[3] if len(sys.argv) > 3 else None

    try:
        remove_background(inp, out, mask_out)
        print(f"OK:{out}")
        sys.exit(0)
    except Exception as e:
        print(f"ERROR:{e}", file=sys.stderr)
        sys.exit(2)
