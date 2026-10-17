"""Include NuGet package metadata, licenses and notices in binary distributions."""

import argparse
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path


def collect(assets_path: Path, output: Path, runtime: str) -> None:
    assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
    roots = [Path(root) for root in assets["packageFolders"]]
    packages = {}
    for identity, metadata in assets["libraries"].items():
        if metadata["type"] != "package":
            continue
        for root in roots:
            package = root / metadata["path"]
            if package.is_dir():
                packages[identity] = package
                break
        else:
            raise FileNotFoundError(f"Restored package not found: {identity}")

    # Self-contained runtime packs are downloaded by the SDK and may be absent
    # from the assets library list. Preserve their license and native notices too.
    for root in roots:
        for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
            runtime_root = root / f"{framework.lower()}.runtime.{runtime}"
            for package in runtime_root.glob("*"):
                if package.is_dir():
                    packages[f"{framework}.Runtime.{runtime}/{package.name}"] = package

    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        if not any(identity.startswith(f"{framework}.Runtime.{runtime}/") for identity in packages):
            raise FileNotFoundError(f"Self-contained runtime license pack not found: {framework} {runtime}")

    output.mkdir(parents=True, exist_ok=True)
    index = ["# Bundled dependency licenses", "",
             "Package license metadata and bundled notice files are included below.", ""]
    for identity, package in sorted(packages.items()):
        destination = output / identity.replace("/", "-")
        destination.mkdir(parents=True, exist_ok=True)
        files = set()
        for nuspec in package.glob("*.nuspec"):
            files.add(nuspec)
            xml = ET.parse(nuspec)
            license_node = next((node for node in xml.iter()
                                 if node.tag.rsplit("}", 1)[-1] == "license"), None)
            if license_node is not None and license_node.attrib.get("type") == "file":
                files.add(package / license_node.text.strip())
            metadata = " ".join(license_node.itertext()).strip() if license_node is not None else "See package metadata"
            index.append(f"- {identity}: {metadata}")
        for candidate in package.rglob("*"):
            name = candidate.name.lower()
            in_notices = any(part.lower() in ("notices", "licenses")
                             for part in candidate.relative_to(package).parts[:-1])
            if candidate.is_file() and (in_notices or name.startswith(("license", "packagelicense", "third-party", "thirdparty", "notice", "copying"))):
                files.add(candidate)
        for source in files:
            target = destination / source.relative_to(package)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    (output / "README.md").write_text("\n".join(index) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("runtime")
    args = parser.parse_args()
    collect(args.assets, args.output, args.runtime)
