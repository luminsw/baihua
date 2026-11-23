"""
safetensors → OpenVINO IR 转换脚本。

用法：
    python convert_safetensors_to_ov.py --src <safetensors目录> [--dst <输出目录>] [--quant int4]

依赖：optimum-intel, openvino, transformers, torch, safetensors, nncf

转换流程：
    1. 用 optimum.intel.OVModelForCausalLM.from_pretrained(export=True) 导出 OpenVINO IR
    2. 可选 INT4 量化（NNCF，适配 Intel Arc 核显 GPU）
    3. save_pretrained 写出 openvino_model.bin / openvino_model.xml + tokenizer 文件
"""

import argparse
import os
import sys
import time
from pathlib import Path


def log(msg: str):
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def main():
    parser = argparse.ArgumentParser(description="safetensors → OpenVINO IR 转换")
    parser.add_argument("--src", required=True, help="源目录（含 safetensors 文件）")
    parser.add_argument("--dst", default=None, help="输出目录（默认同 src）")
    parser.add_argument("--quant", default="int4", choices=["int4", "int8", "fp16", "none"],
                        help="量化级别（默认 int4）")
    args = parser.parse_args()

    src = Path(args.src).resolve()
    dst = Path(args.dst).resolve() if args.dst else src

    if not src.exists():
        log(f"错误：源目录不存在 {src}")
        sys.exit(1)

    log(f"源目录: {src}")
    log(f"输出目录: {dst}")
    log(f"量化: {args.quant}")

    # 检查 safetensors 文件
    safetensors_files = list(src.glob("*.safetensors"))
    if not safetensors_files:
        log(f"错误：{src} 下未找到 .safetensors 文件")
        sys.exit(1)
    log(f"找到 {len(safetensors_files)} 个 safetensors 文件")

    # 检查是否已转换
    ov_bin = dst / "openvino_model.bin"
    if ov_bin.exists():
        log(f"OpenVINO IR 已存在: {ov_bin}，跳过转换")
        sys.exit(0)

    # 导入 optimum-intel
    log("加载 optimum-intel ...")
    try:
        from optimum.intel import OVModelForCausalLM
        import openvino as ov
    except ImportError as e:
        log(f"错误：缺少依赖 {e}，请安装 optimum-intel openvino")
        sys.exit(1)

    # 导出 OpenVINO IR（FP 精度；量化在导出后通过 NNCF compress_weights 完成）
    log("开始导出 OpenVINO IR（可能需要数分钟）...")
    t0 = time.time()

    model = OVModelForCausalLM.from_pretrained(str(src), export=True)

    elapsed = time.time() - t0
    log(f"导出完成，耗时 {elapsed:.0f}s")

    # 保存
    dst.mkdir(parents=True, exist_ok=True)
    log(f"保存到 {dst} ...")
    model.save_pretrained(str(dst))
    del model

    # 可选 INT4/INT8 权重量化（NNCF compress_weights，无需校准数据，适配 Intel Arc 核显）
    if args.quant in ("int4", "int8"):
        log(f"开始 {args.quant} 权重量化（NNCF compress_weights）...")
        import nncf
        ov_xml = dst / "openvino_model.xml"
        ov_bin = dst / "openvino_model.bin"
        core = ov.Core()
        model_ov = core.read_model(str(ov_xml), str(ov_bin))
        log(f"模型已加载，参数量: {len(model_ov.get_parameters())}")

        if args.quant == "int4":
            primary = nncf.CompressWeightsMode.INT4_SYM
            fallback = nncf.CompressWeightsMode.INT4_ASYM
        else:
            primary = nncf.CompressWeightsMode.INT8_SYM
            fallback = nncf.CompressWeightsMode.INT8_ASYM

        try:
            quantized = nncf.compress_weights(model_ov, mode=primary)
        except Exception as e:
            log(f"{args.quant}_SYM 量化失败 ({e})，回退 {args.quant}_ASYM ...")
            quantized = nncf.compress_weights(model_ov, mode=fallback)

        tmp_xml = str(dst / "openvino_model_q.xml")
        tmp_bin = str(dst / "openvino_model_q.bin")
        ov.serialize(quantized, tmp_xml, tmp_bin)
        del quantized
        del model_ov

        os.replace(tmp_bin, str(ov_bin))
        os.replace(tmp_xml, str(ov_xml))
        log(f"{args.quant} 量化完成")

    # 复制 tokenizer 等辅助文件（save_pretrained 通常已处理，但确保齐全）
    import shutil
    for fname in ["tokenizer.json", "tokenizer_config.json", "vocab.json",
                   "merges.txt", "special_tokens_map.json", "chat_template.json",
                   "generation_config.json", "config.json"]:
        s = src / fname
        d = dst / fname
        if s.exists() and not d.exists():
            shutil.copy2(s, d)
            log(f"复制 {fname}")

    # 验证
    ov_bin = dst / "openvino_model.bin"
    ov_xml = dst / "openvino_model.xml"
    if ov_bin.exists() and ov_xml.exists():
        size_mb = ov_bin.stat().st_size / 1024 / 1024
        log(f"转换成功！openvino_model.bin = {size_mb:.1f}MB")
        sys.exit(0)
    else:
        log("错误：转换后未找到 openvino_model.bin/xml")
        sys.exit(1)


if __name__ == "__main__":
    main()