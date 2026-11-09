"""Images of the home page of the website (scripts/build-site.sh). The screenshots, light and dark, English and
French, are the PNG screenshots of the user guide, scaled down to the width they are shown at and saved as WebP in
<site>/images/, about a third of their weight. The icon of the page (<site>/icon.png) is the icon of the application at
128 pixels. Needs Pillow (pip install pillow, or the python3-pil package of Debian and Ubuntu).

    python scripts/docs/site-images.py <images of the guide> <icon of the application> <site folder>
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

# Size of the icon of the page: twice the 36 pixels it is shown at, and more than enough for a browser tab
ICON_SIZE = 128


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__.strip().splitlines()[-1].strip(), file=sys.stderr)
        return 2
    source, icon, site = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])

    with Image.open(icon) as image:
        image.convert("RGB").resize((ICON_SIZE, ICON_SIZE), Image.LANCZOS).save(site / "icon.png", optimize=True)

    target = site / "images"
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
