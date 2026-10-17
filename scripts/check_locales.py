#!/usr/bin/env python3
"""校验语言包：每种语言与简体中文 key 一致、值非空、{0} 占位符一致，并按统一格式重写（排序、两空格缩进、UTF-8 无 BOM）。
用法: python3 scripts/check_locales.py [语言代码...]   不带参数时检查全部 7 种"""
import json, re, sys, pathlib
ROOT = pathlib.Path(__file__).resolve().parent.parent / "src/FftTrainer.Core/Locales"
LANGS = ["zh-Hans", "zh-Hant", "en", "fr", "de", "ja", "ko"]
HOLE = re.compile(r"\{\d+(?::[^}]*)?\}")
bad = 0
for area in ["app", "core"]:
    source = json.loads((ROOT / f"{area}.zh-Hans.json").read_text("utf-8"))
    for lang in sys.argv[1:] or LANGS:
        path = ROOT / f"{area}.{lang}.json"
        raw = path.read_bytes()
        if raw.startswith(b"\xef\xbb\xbf"):
            print(f"{path.name}: 有 BOM"); bad += 1
        data = json.loads(raw.decode("utf-8-sig"))
        for key in sorted(source.keys() - data.keys()):
            print(f"{path.name}: 缺 {key}"); bad += 1
        for key in sorted(data.keys() - source.keys()):
            print(f"{path.name}: 多出 {key}"); bad += 1
        for key, text in data.items():
            if not str(text).strip():
                print(f"{path.name}: {key} 为空"); bad += 1
            if key in source and sorted(HOLE.findall(source[key])) != sorted(HOLE.findall(text)):
                print(f"{path.name}: {key} 占位符不一致"); bad += 1
        path.write_text(json.dumps(dict(sorted(data.items())), ensure_ascii=False, indent=2) + "\n", "utf-8")
print("OK" if not bad else f"{bad} 个问题")
sys.exit(1 if bad else 0)
