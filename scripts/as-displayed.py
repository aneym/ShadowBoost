#!/usr/bin/env python3
"""Apply a gamma ramp read back from the PC to a screen capture.

Windows screen captures hold the pixels before the display's gamma ramp, so
warmth and dimming never show in them. This maps each pixel through the ramp
that pc-install-job.ps1 read from the primary display (ramp-*.json: 256 red,
green and blue entries, 0..65535), giving the colours the panel was sent.

usage: as-displayed.py <screen.png> <ramp.json> <out.png> [--side-by-side <other.png>]
"""
import json
import sys
from PIL import Image


def main(argv):
    if len(argv) not in (4, 6):
        sys.exit(__doc__)
    screen, ramp_path, out = argv[1:4]
    ramp = json.load(open(ramp_path, encoding='utf-8-sig'))
    lut = []
    for channel in ('red', 'green', 'blue'):
        values = ramp[channel]
        if len(values) != 256:
            sys.exit(f'{channel}: expected 256 entries, got {len(values)}')
        lut += [min(255, round(v / 257)) for v in values]
    image = Image.open(screen).convert('RGB').point(lut)
    if len(argv) == 6:
        other = Image.open(argv[5]).convert('RGB')
        both = Image.new('RGB', (other.width + image.width, max(other.height, image.height)))
        both.paste(other, (0, 0))
        both.paste(image, (other.width, 0))
        image = both
    image.save(out)


if __name__ == '__main__':
    main(sys.argv)
