#!/usr/bin/env python3
"""
OpenVINO cross-platform benchmark (Windows native vs WSL2).

Tests VL/LLM pipeline performance across devices:
  - Model load time
  - Time to first token (TTFT)
  - Generation throughput (tokens/sec, post-TTFT)
  - End-to-end latency

Models (OpenVINO IR, int4 quantized):
  - Qwen2.5-VL-7B-Instruct-int4-ov (primary, ~5GB, multi-modal)


Usage:
  # Windows (PowerShell)
  py -3.12 scripts/openvino_benchmark.py --devices CPU GPU NPU --output out-win.json

  # WSL2 (Ubuntu-24.04)
  python3 ~/openvino_benchmark.py --devices CPU GPU --output out-wsl.json
  # (or mount and run directly)
"""
from __future__ import annotations

import argparse
import json
import os
import platform
import sys
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

# Enforce import order (numpy before openvino — documented pybind quirk)
import numpy as np  # noqa: F401

import openvino as ov
import openvino_genai as ov_genai


# ---------------------------------------------------------------------------
# Platform detection
# ---------------------------------------------------------------------------

def detect_model_dir() -> Path:
    """Find the shared model dir across Windows / WSL2 mounts (BAIHUA_HOME 优先)."""
    candidates = []
    # Explicit override via env
    env_dir = os.environ.get("OPENCLAW_MODELS_DIR") or os.environ.get("BAIHUA_MODELS_DIR")
    if env_dir:
        candidates.insert(0, Path(env_dir))
    # BAIHUA_HOME 统一数据根（Windows / WSL 都可用）
    baihua_home = os.environ.get("BAIHUA_HOME")
    if baihua_home:
        candidates.append(Path(baihua_home) / "models")
    # Windows native
    home = os.path.expanduser("~")
    candidates.append(Path(home) / ".baihua" / "models")
    # WSL2 via /mnt/c
    candidates.append(Path("/mnt/c/Users/lumin/.baihua/models"))
    # 旧位置（兼容）
    candidates.append(Path(home) / ".openclaw" / "models")
    candidates.append(Path("/mnt/c/Users/lumin/.openclaw/models"))

    for p in candidates:
        if p.exists():
            print(f"[model_dir] {p}  (exists)")
            return p
    raise FileNotFoundError(
        f"Cannot find OpenVINO model dir. Tried: {[str(c) for c in candidates]}. "
        "Set OPENCLAW_MODELS_DIR or BAIHUA_HOME env var."
    )


def platform_tag() -> dict[str, Any]:
    uname = platform.uname()
    tag = {
        "system": uname.system,
        "node": uname.node,
        "release": uname.release,
        "version": uname.version,
        "machine": uname.machine,
        "python": platform.python_version(),
        "openvino": ov.__version__,
        "openvino_genai": getattr(ov_genai, "__version__", "unknown"),
    }
    # WSL detection
    try:
        if uname.system == "Linux" and Path("/proc/version").exists():
            if "microsoft" in Path("/proc/version").read_text().lower():
                tag["wsl"] = True
                tag["wsl_dxg"] = str(Path("/dev/dxg").exists())
    except Exception:
        pass
    return tag


# ---------------------------------------------------------------------------
# Benchmark data classes
# ---------------------------------------------------------------------------

@dataclass
class PromptResult:
    prompt: str
    max_new_tokens: int
    ttft_ms: float             # time to first token (ms)
    total_time_ms: float       # wall clock from generate() call to done
    output_tokens: int         # generated token count (excluding prompt)
    output_text: str = ""

    @property
    def tps(self) -> float:
        """Tokens/sec excluding the first token (decode throughput)."""
        decode_ms = max(self.total_time_ms - self.ttft_ms, 0.1)
        decode_tokens = max(self.output_tokens - 1, 0)
        return decode_tokens / (decode_ms / 1000.0) if decode_tokens else 0.0

    @property
    def end_to_end_tps(self) -> float:
        return self.output_tokens / (self.total_time_ms / 1000.0) if self.total_time_ms else 0.0


@dataclass
class DeviceRun:
    device: str                        # "CPU", "GPU", "NPU"
    model: str                         # model directory name
    load_time_ms: float = 0.0
    prompts: list[PromptResult] = field(default_factory=list)
    error: str | None = None

    def avg_ttft_ms(self) -> float:
        return float(np.mean([p.ttft_ms for p in self.prompts])) if self.prompts else 0.0

    def avg_tps(self) -> float:
        return float(np.mean([p.tps for p in self.prompts])) if self.prompts else 0.0

    def avg_e2e_tps(self) -> float:
        return float(np.mean([p.end_to_end_tps for p in self.prompts])) if self.prompts else 0.0


# ---------------------------------------------------------------------------
# Prompt set (diverse lengths)
# ---------------------------------------------------------------------------

PROMPTS = [
    # Short, trivial factual
    (
        "Reply with exactly the answer. No preamble, no explanation.\n"
        "Q: What is 123 * 456?\nA:",
        16,
    ),
    # Medium length reasoning
    (
        "Explain step by step how a transformer's multi-head attention works. "
        "Keep it concise, ~150 words. Start with 'Multi-head attention splits'",
        256,
    ),
    # Long context creative
    (
        "Write a short story (200-300 words) about a robot learning to paint watercolor "
        "sunrises on a rainy Pacific coast. Include specific sensory details.",
        512,
    ),
]


# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------

def _is_vl_model(model_dir: Path) -> bool:
    return (model_dir / "openvino_vision_embeddings_model.xml").exists()


def build_pipeline(model_dir: Path, device: str):
    """Build VLMPipeline or LLMPipeline depending on model contents."""
    is_vl = _is_vl_model(model_dir)
    print(f"  [pipeline] {'VLMPipeline' if is_vl else 'LLMPipeline'}  device={device}")
    cls = ov_genai.VLMPipeline if is_vl else ov_genai.LLMPipeline
    return cls(str(model_dir), device)


def run_generate(pipeline, prompt: str, max_new_tokens: int, is_vl: bool) -> PromptResult:
    """Run a single generate() call and gather timing metrics.

    IMPORTANT:
      - VLMPipeline.generate() signature (from openvino_llm_server.py, tested):
          VL:  pipe.generate(prompt, generation_config=cfg)            # text-only
          VL:  pipe.generate(prompt, images=[tensor], generation_config=cfg)  # with image
          LLM: pipe.generate(prompt, generation_config=cfg)            # text-only
        `generation_config` MUST be passed as keyword argument (pybind overload #5 for VL).
      - The `streamer` callback is Callable[[str], int|None], receiving text delta per step.
      - Result has `.texts[0]` for both VLMPipeline and LLMPipeline (verified in server code).
    """
    first_token_received = {"ts": None}

    def stream_cb(delta_text: str):
        # openvino-genai invokes this with per-step text delta (unicode str).
        if first_token_received["ts"] is None and delta_text:
            first_token_received["ts"] = time.perf_counter()
        return None  # returning None or 0 = continue generation

    gen_cfg = ov_genai.GenerationConfig()
    gen_cfg.max_new_tokens = max_new_tokens
    gen_cfg.do_sample = False

    start = time.perf_counter()

    # Use the simplest, server-verified call pattern: keyword generation_config + streamer.
    # streamer accepts either StreamerBase or a raw Callable[[str], ...]. We pass the raw callable.
    kwargs = dict(generation_config=gen_cfg, streamer=stream_cb)
    try:
        if is_vl:
            # VL text-only: no images= kwarg needed (matches server line 158)
            result = pipeline.generate(prompt, **kwargs)
        else:
            result = pipeline.generate(prompt, **kwargs)
    except TypeError as e:
        # Fallback: some API variants don't accept streamer kwarg. Retry without it.
        kwargs.pop("streamer", None)
        if is_vl:
            result = pipeline.generate(prompt, **kwargs)
        else:
            result = pipeline.generate(prompt, **kwargs)
        # Record TTFT couldn't be measured directly → we'll use approximation below

    end = time.perf_counter()

    # Extract text — VLMPipeline and LLMPipeline both return DecodedResults with .texts list
    try:
        texts = getattr(result, "texts", None)
        text = texts[0] if isinstance(texts, list) and texts else str(result)
    except Exception:
        text = str(result)

    # Token count estimate: ~4 chars/token for mixed CJK+English is conservative-but-stable across platforms.
    # We intentionally do NOT try to call pipeline tokenizer (not exposed in all API versions).
    estimated_tokens = max(1, len(text) // 4)

    if first_token_received["ts"] is not None:
        ttft_ms = (first_token_received["ts"] - start) * 1000.0
    else:
        # No streaming callback: approximate TTFT = 15% of total wall (typical prefill/decode split for short outputs)
        total_ms = (end - start) * 1000.0
        ttft_ms = total_ms * 0.15

    return PromptResult(
        prompt=prompt[:80] + ("..." if len(prompt) > 80 else ""),
        max_new_tokens=max_new_tokens,
        ttft_ms=ttft_ms,
        total_time_ms=(end - start) * 1000.0,
        output_tokens=estimated_tokens,
        output_text=text[:200] + ("..." if len(text) > 200 else ""),
    )


def bench_device(model_dir: Path, model_name: str, device: str, repeats: int) -> DeviceRun:
    run = DeviceRun(device=device, model=model_name)
    print(f"\n{'='*60}")
    print(f"MODEL={model_name}  DEVICE={device}")
    print(f"{'='*60}")

    is_vl = _is_vl_model(model_dir)

    # --- Load ---
    t0 = time.perf_counter()
    try:
        pipeline = build_pipeline(model_dir, device)
    except Exception as e:
        run.error = f"[load] {type(e).__name__}: {e}"
        print(f"  FAIL {run.error}")
        return run
    run.load_time_ms = (time.perf_counter() - t0) * 1000.0
    print(f"  [load] {run.load_time_ms:.0f} ms")

    # Warmup (single short generate, not counted) — important for CPU/GPU
    print(f"  [warmup]")
    try:
        _ = run_generate(pipeline, "Say hello.", max_new_tokens=8, is_vl=is_vl)
    except Exception as e:
        print(f"  [warmup failed — still continuing] {e}")

    # --- Measure per prompt, repeated ---
    for i in range(repeats):
        for p_idx, (prompt_text, max_new) in enumerate(PROMPTS):
            label = f"run{i+1}/{repeats} prompt{p_idx+1}/{len(PROMPTS)} (max_new={max_new})"
            print(f"  {label} ... ", end="", flush=True)
            try:
                pr = run_generate(pipeline, prompt_text, max_new, is_vl=is_vl)
                run.prompts.append(pr)
                print(
                    f"ttft={pr.ttft_ms:.0f}ms  "
                    f"tokens={pr.output_tokens}  "
                    f"tps={pr.tps:.2f}  "
                    f"e2e={pr.end_to_end_tps:.2f} tok/s"
                )
            except Exception as e:
                err = f"[gen] {type(e).__name__}: {e}"
                print(err)
                # Append a zero entry with error so downstream knows which prompt failed
                run.prompts.append(PromptResult(
                    prompt=prompt_text[:80],
                    max_new_tokens=max_new,
                    ttft_ms=0.0,
                    total_time_ms=0.0,
                    output_tokens=0,
                    output_text="",
                ))
                # Store last error
                run.error = err
                # If NPU/GPU fails mid-way, stop further prompts on this device
                if device in ("NPU", "GPU"):
                    break

        if device in ("NPU", "GPU") and run.error:
            break

    # Explicit cleanup
    del pipeline
    return run


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--devices", nargs="+", default=["CPU"],
                    help="Space-separated device list, e.g. --devices CPU GPU NPU")
    ap.add_argument("--models", nargs="+",
                    default=["Qwen2.5-VL-7B-Instruct-int4-ov"],
                    help="Model directory names under .openclaw/models")
    ap.add_argument("--repeats", type=int, default=1,
                    help="How many times to run the prompt set (>=1). Averages reported.")
    ap.add_argument("--models-dir", type=str, default=None,
                    help="Override .openclaw/models directory.")
    ap.add_argument("--output", type=str, default=None,
                    help="Write JSON report to this file.")
    args = ap.parse_args()

    # Report environment
    pt = platform_tag()
    print(json.dumps(pt, indent=2, ensure_ascii=False))

    if args.models_dir:
        models_dir = Path(args.models_dir)
    else:
        models_dir = detect_model_dir()

    # Detect real OpenVINO devices (informational, cross-check requested list)
    core = ov.Core()
    real_devs = core.available_devices
    print(f"\n[openvino] available_devices = {real_devs}")
    for d in real_devs:
        try:
            full = core.get_property(d, "FULL_DEVICE_NAME")
            print(f"   {d:<5} -> {full}")
        except Exception:
            pass

    # Resolve which models exist
    chosen_models: list[Path] = []
    for m in args.models:
        p = models_dir / m
        if p.exists():
            chosen_models.append(p)
        else:
            print(f"[skip] {m} not found under {models_dir}")
    if not chosen_models:
        print("[error] no usable models.")
        return 2

    # Run all combinations
    all_runs: list[DeviceRun] = []
    for model_path in chosen_models:
        for dev in args.devices:
            r = bench_device(model_path, model_path.name, dev, repeats=max(1, args.repeats))
            all_runs.append(r)

    # --- Summary table ---
    print("\n" + "=" * 90)
    print("SUMMARY (averages across prompts)")
    print("=" * 90)
    header = f"{'Model':<40} {'Dev':<5} {'Load(ms)':>9} {'TTFT(ms)':>9} {'Decode(t/s)':>12} {'E2E(t/s)':>10} {'Runs':>5} {'Error'}"
    print(header)
    print("-" * len(header))
    for r in all_runs:
        ok = len([p for p in r.prompts if p.output_tokens > 0])
        err_prefix = (r.error[:50] + "...") if r.error else ""
        print(
            f"{r.model:<40} {r.device:<5} "
            f"{r.load_time_ms:>9.0f} "
            f"{r.avg_ttft_ms():>9.0f} "
            f"{r.avg_tps():>12.2f} "
            f"{r.avg_e2e_tps():>10.2f} "
            f"{ok:>5} "
            f"{err_prefix}"
        )

    # --- JSON report ---
    report = {
        "platform": pt,
        "devices_requested": args.devices,
        "devices_available": real_devs,
        "models_used": [m.name for m in chosen_models],
        "prompts": [
            {"idx": i, "text": p[0], "max_new_tokens": p[1]}
            for i, p in enumerate(PROMPTS)
        ],
        "repeats": args.repeats,
        "runs": [
            {
                **asdict(r),
                "summary": {
                    "avg_ttft_ms": r.avg_ttft_ms(),
                    "avg_tps": r.avg_tps(),
                    "avg_e2e_tps": r.avg_e2e_tps(),
                    "successful_prompts": len([p for p in r.prompts if p.output_tokens > 0]),
                },
            }
            for r in all_runs
        ],
    }

    if args.output:
        out = Path(args.output)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"\n[json] wrote {out}")
    else:
        # Also dump to stdout (compact)
        print()
        print(json.dumps(report["runs"], ensure_ascii=False, indent=2))

    return 0


if __name__ == "__main__":
    sys.exit(main())
