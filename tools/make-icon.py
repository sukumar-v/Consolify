# Generates Consolify.ico and consolify-icon.png (the app icon and its README master).
#   pip install Pillow
#   python tools/make-icon.py     -- writes into the current directory
# Copy Consolify.ico into Consolify/ and consolify-icon.png to the repo root.

import PIL.Image
import PIL.ImageDraw

# Create high-res square image for ICO generation
size = (512, 512)
image = PIL.Image.new("RGBA", size, (0, 0, 0, 0))
draw = PIL.ImageDraw.Draw(image)

# Dimensions
w, h = size

# Outer app icon rounded rectangle background
bg_color = (20, 24, 33, 255)
corner_radius = 90
draw.rounded_rectangle([20, 20, 492, 492], radius=corner_radius, fill=bg_color)

# Create mask for the flat symbol shapes (C ring, joystick thumbstick, four action dots)
mask = PIL.Image.new("L", size, 0)
mask_draw = PIL.ImageDraw.Draw(mask)

# --- Draw the Symbol onto Mask ---
# 1. Clean 'C' ring (no inner ring shadow/layer)
center_x, center_y = 230, 256
r_out = 160
r_in = 110

mask_draw.ellipse([center_x - r_out, center_y - r_out, center_x + r_out, center_y + r_out], fill=255)
mask_draw.ellipse([center_x - r_in, center_y - r_in, center_x + r_in, center_y + r_in], fill=0)

# Cut out right wedge (between -38 deg and 38 deg approx)
mask_draw.pieslice([center_x - r_out - 10, center_y - r_out - 10, center_x + r_out + 10, center_y + r_out + 10], start=-38, end=38, fill=0)

# Rounded ends for 'C'
import math
cap_r = (r_out - r_in) / 2

top_angle = math.radians(-38)
top_cx = center_x + (r_out + r_in)/2 * math.cos(top_angle)
top_cy = center_y + (r_out + r_in)/2 * math.sin(top_angle)
mask_draw.ellipse([top_cx - cap_r, top_cy - cap_r, top_cx + cap_r, top_cy + cap_r], fill=255)

bot_angle = math.radians(38)
bot_cx = center_x + (r_out + r_in)/2 * math.cos(bot_angle)
bot_cy = center_y + (r_out + r_in)/2 * math.sin(bot_angle)
mask_draw.ellipse([bot_cx - cap_r, bot_cy - cap_r, bot_cx + cap_r, bot_cy + cap_r], fill=255)

# 2. Joystick Top-View (Base outer cap & inner textured thumbstick)
joy_cx, joy_cy = center_x, center_y
joy_outer_r = 75
joy_inner_r = 52

# Outer base ring of thumbstick
mask_draw.ellipse([joy_cx - joy_outer_r, joy_cy - joy_outer_r, joy_cx + joy_outer_r, joy_cy + joy_outer_r], fill=255)

# 3. Four Action Dots on the Right
dot_r = 21
dots_center_x = 385
dots_center_y = 256
spread = 42

dot_positions = [
    (dots_center_x, dots_center_y - spread), # Top
    (dots_center_y, dots_center_y + spread), # Bottom -> fixed syntax
    (dots_center_x - spread, dots_center_y), # Left
    (dots_center_x + spread, dots_center_y)  # Right
]

# Correcting bottom dot coordinate
dot_positions[1] = (dots_center_x, dots_center_y + spread)

for dx, dy in dot_positions:
    mask_draw.ellipse([dx - dot_r, dy - dot_r, dx + dot_r, dy + dot_r], fill=255)

# --- Create Main Warm Gradient ---
gradient = PIL.Image.new("RGBA", size, (0, 0, 0, 0))
g_draw = PIL.ImageDraw.Draw(gradient)

c1 = (252, 186, 110) # Warm golden orange top
c2 = (235, 120, 105) # Soft coral pink bottom

for y in range(h):
    t = y / float(h)
    r = int(c1[0] * (1 - t) + c2[0] * t)
    g = int(c1[1] * (1 - t) + c2[1] * t)
    b = int(c1[2] * (1 - t) + c2[2] * t)
    g_draw.line([(0, y), (w, y)], fill=(r, g, b, 255))

# Composite logo mark using mask
image.paste(gradient, (0, 0), mask)

# Add detail to joystick top-view (concentric ring / thumb indent detail)
joy_overlay = PIL.Image.new("RGBA", size, (0, 0, 0, 0))
joy_draw = PIL.ImageDraw.Draw(joy_overlay)
# Draw subtle inner ring for thumb cap grip
joy_draw.ellipse([joy_cx - joy_inner_r, joy_cy - joy_inner_r, joy_cx + joy_inner_r, joy_cy + joy_inner_r], fill=(0, 0, 0, 45))
image.alpha_composite(joy_overlay)

# Save updated ICO with version suffix
image.save("Consolify.ico", format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
print("Consolify.ico created successfully!")

# Also keep the 512 master for the README
image.save("consolify-icon.png", format="PNG")
print("consolify-icon.png created successfully!")
