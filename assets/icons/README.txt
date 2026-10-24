Iconos de WG-RDP / RN VPN-RDP.

Fuente editable: ../img/ (SVG + PNG 48/96/192/512).
app.ico se genera desde rn-vpn-rdp-icon-512.png (tamaños 16–256):

  python3 -c "from PIL import Image; from pathlib import Path; p=Path('.'); \
    img=Image.open(p/'rn-vpn-rdp-icon-512.png').convert('RGBA'); \
    img.save(p/'app.ico', format='ICO', sizes=[(256,256),(48,48),(32,32),(16,16)])"

Requiere python3-pil (Debian) o Pillow.
