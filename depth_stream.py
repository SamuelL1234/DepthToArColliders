#!/usr/bin/env python3
"""
Depth Anything V2 → Unity Depth Streamer

Usage:
    python depth_stream.py --source 0 --port 9999 --model-size base --preview
"""

import argparse
import cv2
import numpy as np
import signal
import socket
import struct
import threading
import time
import torch

running = True

def signal_handler(sig, frame):
    global running
    print("\nStopping...")
    running = False

signal.signal(signal.SIGINT, signal_handler)


def load_depth_anything(model_size="small", scene="indoor", device="cuda"):
    import sys
    sys.path.insert(0, 'metric_depth')
    from depth_anything_v2.dpt import DepthAnythingV2
    model_configs = {
        "small": {"encoder": "vits", "features": 64, "out_channels": [48, 96, 192, 384]},
        "base": {"encoder": "vitb", "features": 128, "out_channels": [96, 192, 384, 768]},
        "large": {"encoder": "vitl", "features": 256, "out_channels": [256, 512, 1024, 1024]},
    }
    cfg = model_configs[model_size]
    encoder_short = cfg["encoder"][3]

    dataset = "hypersim" if scene == "indoor" else "vkitti"
    max_depth = 20 if scene == "indoor" else 80

    model = DepthAnythingV2(**cfg, max_depth=max_depth)
    ckpt = f"checkpoints/depth_anything_v2_metric_{dataset}_vit{encoder_short}.pth"
    print(f"Loading: {ckpt} (scene={scene}, max_depth={max_depth}m)")
    model.load_state_dict(torch.load(ckpt, map_location="cpu"))
    model = model.to(device).eval()
    print(f"Loaded metric depth model ({model_size}, {scene}) on {device}")
    return model


def estimate_depth(model, frame):
    with torch.no_grad():
        return model.infer_image(frame).astype(np.float32)


class UnityStreamer:
    def __init__(self, port=9999):
        self.port = port
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind(("127.0.0.1", port))
        self.server.listen(1)
        self.server.settimeout(1.0)
        self.client = None
        self.lock = threading.Lock()
        self.accept_thread = threading.Thread(target=self._accept_loop, daemon=True)
        self.accept_thread.start()
        print(f"Depth server listening on 127.0.0.1:{port}")

    def _accept_loop(self):
        global running
        while running:
            try:
                client, addr = self.server.accept()
                with self.lock:
                    if self.client: self.client.close()
                    self.client = client
                print(f"Unity connected from {addr}")
            except socket.timeout:
                continue

    def send_frame(self, depth_map, color_frame, camera_params):
        with self.lock:
            if self.client is None: return False
        h, w = depth_map.shape
        fx, fy, cx, cy = camera_params
        _, jpeg_buf = cv2.imencode('.jpg', color_frame, [cv2.IMWRITE_JPEG_QUALITY, 70])
        jpeg_bytes = jpeg_buf.tobytes()
        header = struct.pack("<II4f", w, h, fx, fy, cx, cy)
        depth_data = depth_map.astype(np.float32).tobytes()
        jpeg_header = struct.pack("<I", len(jpeg_bytes))
        try:
            with self.lock:
                self.client.sendall(header + depth_data + jpeg_header + jpeg_bytes)
            return True
        except (BrokenPipeError, ConnectionResetError, OSError):
            with self.lock: self.client = None
            print("Unity disconnected")
            return False

    def close(self):
        with self.lock:
            if self.client: self.client.close()
        self.server.close()


def main():
    global running
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default="0")
    parser.add_argument("--port", type=int, default=9999)
    parser.add_argument("--model-size", default="base", choices=["small", "base", "large"])
    parser.add_argument("--scene", default="indoor", choices=["indoor", "outdoor"],
                        help="indoor = hypersim (max 20m), outdoor = vkitti (max 80m)")
    parser.add_argument("--depth-width", type=int, default=320)
    parser.add_argument("--depth-height", type=int, default=240)
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--depth-scale", type=float, default=10.0)
    parser.add_argument("--device", default="cuda")
    args = parser.parse_args()

    device = args.device if torch.cuda.is_available() else "cpu"
    model = load_depth_anything(args.model_size, args.scene, device)

    try: source = int(args.source)
    except ValueError: source = args.source

    cap = cv2.VideoCapture(source)
    if not cap.isOpened(): print(f"Error: Cannot open {args.source}"); return
    ret, test_frame = cap.read()
    if not ret: print("Error: Cannot read from camera"); return

    h0, w0 = test_frame.shape[:2]
    fx = fy = max(w0, h0) * 0.8
    cx, cy = w0 / 2, h0 / 2
    sx = args.depth_width / w0
    sy = args.depth_height / h0
    cam_params = (fx * sx, fy * sy, cx * sx, cy * sy)

    print(f"Camera: {w0}x{h0} | Depth: {args.depth_width}x{args.depth_height}")
    streamer = UnityStreamer(args.port)
    print("Press Ctrl+C to stop.\n")

    frame_count = 0
    fps_timer = time.time()

    while running:
        ret, frame = cap.read()
        if not ret:
            if isinstance(source, int): continue
            else: break

        depth = estimate_depth(model, frame)
        depth_small = cv2.resize(depth, (args.depth_width, args.depth_height), interpolation=cv2.INTER_NEAREST)
        color_small = cv2.resize(frame, (args.depth_width, args.depth_height))
        streamer.send_frame(depth_small, color_small, cam_params)

        if args.preview:
            depth_vis = (np.clip(depth / args.depth_scale, 0, 1) * 255).astype(np.uint8)
            depth_colored = cv2.applyColorMap(depth_vis, cv2.COLORMAP_INFERNO)
            depth_colored = cv2.resize(depth_colored, (w0, h0))
            combined = np.hstack([frame, depth_colored])
            cv2.imshow("Depth Stream (q to quit)", combined)
            if cv2.waitKey(1) & 0xFF == ord("q"): break

        frame_count += 1
        if frame_count % 30 == 0:
            print(f"  FPS: {frame_count/(time.time()-fps_timer):.1f}", end="\r")

    cap.release(); streamer.close()
    if args.preview: cv2.destroyAllWindows()
    print(f"\nDone. {frame_count} frames.")

if __name__ == "__main__":
    main()
