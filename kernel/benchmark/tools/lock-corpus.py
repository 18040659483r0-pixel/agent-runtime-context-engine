#!/usr/bin/env python3
"""实验语料锁的「锁文件」生成 / 校验工具。

锁什么：`benchmark.config.json` 的 `corpus.lock.allowedSources` 里那三个语料文件的 sha256。
为什么：测试工程只允许使用仓库内的 10K 切片（主人 2026-09-14 定死）；
       语料一改，历史结论就不可复算 —— 所以改语料必须**同步改锁**，让差异可见。

用法：
    python3 benchmark/tools/lock-corpus.py --update   # 改语料后重新锁（会打印规模）
    python3 benchmark/tools/lock-corpus.py --check     # 校验（CI / 人肉入场仪式）
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

CFG = "AgentRuntime.Benchmark/benchmark.config.json"


def load(base: Path) -> tuple[dict, Path]:
    cfg_path = base / CFG
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    return cfg, cfg_path


def lock_files(cfg: dict) -> list[str]:
    return list(cfg["corpus"]["lock"]["allowedSources"])


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=str(Path(__file__).resolve().parents[1]))  # benchmark/
    ap.add_argument("--update", action="store_true")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    base = Path(args.base)
    cfg, _ = load(base)
    lock_spec = cfg["corpus"]["lock"]
    lock_path = base / "AgentRuntime.Benchmark" / lock_spec["fingerprintFile"]
    cpt = lock_spec["charsPerToken"]
    cap = lock_spec["maxFrozenTokens"]

    files, total_chars, bad = {}, 0, 0
    for rel in lock_files(cfg):
        p = base / "AgentRuntime.Benchmark" / rel
        if not p.exists():
            print(f"[锁] ❌ 缺文件 {rel}", file=sys.stderr)
            bad += 1
            continue
        files[rel] = "sha256:" + hashlib.sha256(p.read_bytes()).hexdigest()
        total_chars += len(p.read_text(encoding="utf-8"))

    tokens = total_chars / cpt

    if args.update:
        lock_path.write_text(
            json.dumps({
                "_comment": "实验语料锁：测试工程只允许这三个文件；改语料必须同步更新本文件"
                            "（python3 benchmark/tools/lock-corpus.py --update）。",
                "files": files,
            }, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8")
        print(f"[锁] ✅ 已写入 {lock_path.name}（{len(files)} 个文件）")
    elif args.check:
        old = json.loads(lock_path.read_text(encoding="utf-8"))["files"] if lock_path.exists() else {}
        for rel, h in files.items():
            if old.get(rel) != h:
                print(f"[锁] ❌ 指纹不符：{rel}", file=sys.stderr)
                bad += 1
        if not old:
            print(f"[锁] ❌ 缺锁文件 {lock_path}", file=sys.stderr)
            bad += 1

    print(f"[锁] 语料合计 {total_chars} 字符 ≈ {tokens:.0f} token"
          f"（上限 {cap}，换算 {cpt} 字符/token）{'  ✅ 在上限内' if tokens <= cap else '  ❌ 超上限'}")

    if tokens > cap:
        bad += 1
    if bad:
        print("[锁] ❌ 不合格", file=sys.stderr)
        return 1
    print("[锁] ✅ 通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
