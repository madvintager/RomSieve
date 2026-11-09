"""Screenshots of the home page of the website (scripts/build-site.sh), light and dark, English and French: the PNG
screenshots of the user guide, scaled down to the width they are shown at and saved as WebP, about a third of their
weight. Needs Pillow (pip install pillow, or the python3-pil package of Debian and Ubuntu).

    python scripts/docs/site-images.py <images of the guide> <output folder>
"""
import sys
from pathlib import Path

from PIL import Image

# Screenshot of the guide, and the width it gets: the banner shows the window at most 1150 pixels wide, a feature
# its screenshot at most 650 (the widths stay sharp on a screen of double density for the features)
SCREENSHOTS = {
    "window": 1600,
    "simulation_preview": 1200,
    "1g1r_settings": 1200,
    "output_settings": 1200,
}


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip().splitlines()[-1].strip(), file=sys.stderr)
        return 2
    source, target = Path(sys.argv[1]), Path(sys.argv[2])
    for folder in ("", "fr"):
        (target / folder).mkdir(parents=True, exist_ok=True)
        for name, width in SCREENSHOTS.items():
            for variant in (name, name + "_dark"):
                with Image.open(source / folder / (variant + ".png")) as image:
                    image = image.convert("RGB")
                    height = round(image.height * width / image.width)
                    scaled = image.resize((width, height), Image.LANCZOS)
                    scaled.save(target / folder / (variant + ".webp"), "WEBP", quality=85, method=6)
    return 0


if __name__ == "__main__":
    sys.exit(main())
