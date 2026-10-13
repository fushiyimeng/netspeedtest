#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
打包发布 —— 一键打出三个发布包
================================================================================

  dist\
    netspeedtest-<版本>-full-standalone.zip     免装 .NET（自带运行时，约 45MB）
    netspeedtest-<版本>-lite-need-dotnet.zip    需要先装 .NET 10 桌面运行时（约 130KB）
    netspeedtest-<版本>-source.zip              源码（双击 编译.bat 就能自己编）

用法：  python 打包发布.py

版本号从 源码\NetSpeedTest.csproj 的 <Version> 读，改那里就行。

注意：打完整版必须带 -p:SkipExeCopy=true，否则 csproj 里那个复制目标会把工具
      根目录里日常用的精简版 exe 覆盖成 45MB 的分发版。
"""

import os
import re
import shutil
import subprocess
import sys
import zipfile

BASE = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(BASE, "dist")
PROJ = os.path.join(BASE, "源码", "NetSpeedTest.csproj")


def read_version():
    try:
        text = open(PROJ, encoding="utf-8").read()
        m = re.search(r"<Version>([^<]+)</Version>", text)
        return m.group(1) if m else "0.0.0"
    except OSError:
        return "0.0.0"


def publish(out_dir, self_contained):
    args = [
        "dotnet", "publish", PROJ,
        "-c", "Release", "-r", "win-x64",
        "--self-contained", "true" if self_contained else "false",
        "-p:PublishSingleFile=true",
        "-p:PublishTrimmed=false",      # WinForms 一 trim 就是编译错误
        "-p:SkipExeCopy=true",          # 别覆盖根目录那个日常用的 exe
    ]
    if self_contained:
        # 单文件包再压一道：不压 150MB+，压完 45MB 左右（代价是首次启动慢一点点）
        args.append("-p:EnableCompressionInSingleFile=true")
    args += ["-o", out_dir, "--nologo", "-v", "quiet"]

    print("  " + ("完整版" if self_contained else "简洁版") + " ...", flush=True)
    r = subprocess.run(args, cwd=BASE)
    if r.returncode != 0:
        sys.exit("  × 编译失败，退出码 %d" % r.returncode)


def add(zf, path, arcname):
    if not os.path.exists(path):
        sys.exit("  × 缺文件：%s" % path)
    zf.write(path, arcname)


def build_zips(version):
    made = []

    # 源码包
    p = os.path.join(DIST, "netspeedtest-%s-source.zip" % version)
    with zipfile.ZipFile(p, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in ("README.md", "说明.txt", "安装说明.txt", "编译.bat",
                  "打包发布.py", ".gitignore", "screenshot.png"):
            add(z, os.path.join(BASE, f), f)
        srcdir = os.path.join(BASE, "源码")
        for f in sorted(os.listdir(srcdir)):
            if f.endswith((".cs", ".csproj")):
                add(z, os.path.join(srcdir, f), "源码/" + f)
    made.append(p)

    # 简洁版（需要 .NET）
    p = os.path.join(DIST, "netspeedtest-%s-lite-need-dotnet.zip" % version)
    with zipfile.ZipFile(p, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        add(z, os.path.join(DIST, "lite", "NetSpeedTest.exe"), "网速测试.exe")
        add(z, os.path.join(BASE, "说明.txt"), "说明.txt")
        add(z, os.path.join(BASE, "安装说明.txt"), "安装说明.txt")
    made.append(p)

    # 完整版（免装 .NET）
    p = os.path.join(DIST, "netspeedtest-%s-full-standalone.zip" % version)
    with zipfile.ZipFile(p, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        add(z, os.path.join(DIST, "full", "NetSpeedTest.exe"), "网速测试.exe")
        add(z, os.path.join(BASE, "说明.txt"), "说明.txt")
    made.append(p)

    return made


def main():
    version = read_version()
    print("版本号：%s" % version)
    print("清理 dist\\ ...")
    shutil.rmtree(DIST, ignore_errors=True)
    os.makedirs(os.path.join(DIST, "lite"))
    os.makedirs(os.path.join(DIST, "full"))

    print("编译：")
    publish(os.path.join(DIST, "lite"), self_contained=False)
    publish(os.path.join(DIST, "full"), self_contained=True)

    print("打包：")
    made = build_zips(version)

    print()
    print("=== 打好了 ===")
    for p in made:
        print("  %8.2f MB  %s" % (os.path.getsize(p) / 1e6, os.path.basename(p)))
    print()
    print("下一步：把这三个 zip 传到 GitHub Release 的附件里。")


if __name__ == "__main__":
    main()
