"""Exercise image pulls and native build arguments without a Docker daemon or personal data."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


MOCK_DOCKER = """#!/bin/sh
printf '%s' "$1" >> docker.calls
shift
for arg in "$@"; do printf '\\t%s' "$arg" >> docker.calls; done
printf '\\n' >> docker.calls
case "$(tail -n 1 docker.calls | cut -f 1)" in
    container) exit 1 ;;
    info) printf '%s\\n' "$FAKE_ARCH" ;;
    pull) if [ "$FAKE_PULL_FAIL" = 1 ]; then exit 42; fi ;;
    run) printf 'fixture-container\\n' ;;
esac
"""


def verify(shell: str, arch: str, image: bool = False, fail_pull: bool = False) -> None:
    repository = Path(__file__).resolve().parents[2]
    with tempfile.TemporaryDirectory(prefix="ergo-update-smoke-") as temporary:
        root = Path(temporary).resolve()
        assert root.parent == Path(tempfile.gettempdir()).resolve()
        (root / "deploy").mkdir()
        (root / "bin").mkdir()
        (root / "data").mkdir()
        shutil.copyfile(repository / "deploy/update.sh", root / "deploy/update.sh")
        docker = root / "bin/docker"
        docker.write_text(MOCK_DOCKER, encoding="utf-8", newline="\n")
        docker.chmod(0o755)
        config = root / "private-config.json"
        config.write_text('{"fixture":"preserve-me"}', encoding="utf-8")
        (root / ".env").write_text("CONFIG_FILE=./private-config.json\nWEB_PORT=8080\n", encoding="utf-8")
        (root / "data/keep.txt").write_text("persistent fixture", encoding="utf-8")
        original = config.read_bytes()
        reference = "ghcr.io/compunited/ergostravalogger:main"
        arguments = ["--image", reference] if image else []
        environment = dict(os.environ, FAKE_ARCH=arch, FAKE_PULL_FAIL="1" if fail_pull else "0")
        result = subprocess.run(
            [shell, "-c", 'PATH="$PWD/bin:$PATH"; export PATH; exec sh deploy/update.sh "$@"', "update-test", *arguments],
            cwd=root, env=environment, capture_output=True, text=True, timeout=15,
        )
        calls = [line.split("\t") for line in (root / "docker.calls").read_text().splitlines()]
        assert config.read_bytes() == original, "The private config must survive updates."
        assert (root / "data/keep.txt").read_text() == "persistent fixture"
        if fail_pull:
            assert result.returncode != 0 and not any(call[0] in {"stop", "run", "build"} for call in calls), calls
            return
        assert result.returncode == 0, result.stderr
        run = next(call for call in calls if call[0] == "run")
        assert any("dst=/config/config.json,readonly" in arg and "private-config.json" in arg for arg in run), run
        assert any("dst=/data" in arg for arg in run), run
        if image:
            assert ["pull", reference] in calls and run[-1] == reference, calls
            assert not any(call[0] in {"info", "build"} for call in calls), calls
        else:
            target = "amd64" if arch in {"x86_64", "amd64"} else "arm64"
            build = next(call for call in calls if call[0] == "build")
            assert f"BUILDPLATFORM=linux/{target}" in build and f"TARGETARCH={target}" in build, build


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--shell", default=shutil.which("sh"))
    options = parser.parse_args()
    if not options.shell:
        parser.error("A POSIX shell is required.")
    for architecture in ("x86_64", "amd64", "aarch64", "arm64"):
        verify(options.shell, architecture)
    verify(options.shell, "x86_64", image=True)
    verify(options.shell, "x86_64", image=True, fail_pull=True)
    print("Update helper: native architectures, image pull and persistent mounts passed.")
