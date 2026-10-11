"""Container-only raster conversion; stdin image bytes, stdout PNG, no files."""
import io
import sys
import warnings
from PIL import Image

Image.MAX_IMAGE_PIXELS = 40_000_000
warnings.simplefilter('error', Image.DecompressionBombWarning)
source = sys.stdin.buffer.read(50 * 1024 * 1024 + 1)
if len(source) > 50 * 1024 * 1024:
    raise ValueError('Image exceeds the conversion limit')
with Image.open(io.BytesIO(source), formats=['TIFF', 'JPEG', 'BMP', 'GIF']) as image:
    image.convert('RGB').save(sys.stdout.buffer, format='PNG')
