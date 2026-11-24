"""Stage the companion viewer MSIX for either game."""

from __future__ import annotations

import base64
import hashlib
import re
import shutil
import tempfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree

PACKAGE = "CSLModernMapRenderer.msix"
IDENTITY = "renderer-package.txt"
ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PACKAGE = ROOT / "build" / "local_payload" / PACKAGE
_MANIFEST = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"


def package_identity(source: Path) -> str:
    with zipfile.ZipFile(source) as archive:
        manifest = ElementTree.fromstring(archive.read("AppxManifest.xml"))
        archive.getinfo("CSLModernMapRenderer.exe")
        if "resources.pri" not in archive.namelist():
            raise ValueError("Viewer MSIX is missing resources.pri; rebuild it with tools/renderer_msix.py.")
    identity = manifest.find(_MANIFEST + "Identity")
    app = manifest.find(f"{_MANIFEST}Applications/{_MANIFEST}Application[@Id='Renderer']")
    if identity is None or app is None or app.get("Executable") != "CSLModernMapRenderer.exe":
        raise ValueError("MSIX must contain the CSLModernMap renderer application.")
    name, publisher, version = (identity.attrib[key] for key in ("Name", "Publisher", "Version"))
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){3}", version):
        raise ValueError(f"Invalid MSIX version: {version}")
    # Windows publisher ID: first 64 SHA-256 bits of the UTF-16LE publisher, in Windows base32.
    digest = hashlib.sha256(publisher.encode("utf-16le")).digest()[:8]
    alphabet = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", "0123456789abcdefghjkmnpqrstvwxyz")
    publisher_id = base64.b32encode(digest).decode("ascii").rstrip("=").translate(alphabet)
    return f"{name}_{publisher_id}!Renderer\n{version}\n"


def stage_package(source: Path, destination: Path) -> None:
    identity = package_identity(source)
    destination.mkdir(parents=True, exist_ok=True)
    target = destination / PACKAGE
    if source.resolve() != target.resolve():
        with tempfile.NamedTemporaryFile(dir=destination, delete=False) as temp:
            temporary = Path(temp.name)
        try:
            shutil.copyfile(source, temporary)
            temporary.replace(target)
        finally:
            temporary.unlink(missing_ok=True)
    (destination / IDENTITY).write_text(identity, encoding="utf-8", newline="\n")


def main() -> None:
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--msix", type=Path, default=DEFAULT_PACKAGE)
    parser.add_argument("--target", type=Path, default=ROOT / "CS2")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if args.dry_run:
        print(package_identity(args.msix))
    else:
        stage_package(args.msix, args.target)


if __name__ == "__main__":
    main()
