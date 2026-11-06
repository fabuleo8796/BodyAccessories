"""Builds the Blender template for Body Accessories: a stand-in Flippy at the game's exact size and place, and an
"Accessory" collection (with an example halo) for the player's own models.
Run:  blender --background --factory-startup --python make_template.py -- <out.blend>

Axes: Blender +Z = up the phone, Blender -Y = out of the screen (Blender's Front view looks at the screen),
Blender +X = the phone's right. 1 Blender unit = 1 game unit. The mod maps this onto Flippy's body
(game local = (x, -y, z)). Sizes come from the game's cover meshes (fih-modding-kit CLAUDE.md, "Phone shape"):
x +-0.436, front 0.190 / back 0.167, bottom -0.997 / top 1.052, corners rounded ~0.17, screen z 0.16-0.70.
The stand-in is our own simple model, not the game's mesh."""
import math
import sys
import bpy

out = sys.argv[sys.argv.index("--") + 1]

for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)
for col in list(bpy.data.collections):
    bpy.data.collections.remove(col)

scene = bpy.context.scene
scene.unit_settings.system = "METRIC"
scene.unit_settings.scale_length = 1.0

ref = bpy.data.collections.new("Flippy (reference, not exported)")
acc = bpy.data.collections.new("Accessory")
scene.collection.children.link(ref)
scene.collection.children.link(acc)

def material(name, rgb, emit=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = 0.55
    if emit > 0:
        key = "Emission Color" if "Emission Color" in bsdf.inputs else "Emission"
        bsdf.inputs[key].default_value = (*rgb, 1)
        bsdf.inputs["Emission Strength"].default_value = emit
    m.diffuse_color = (*rgb, 1)  # the viewport's solid colour
    return m

def put(obj, col):
    for c in obj.users_collection:
        c.objects.unlink(obj)
    col.objects.link(obj)
    return obj

def srgb(h):
    h = h.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return tuple(((v + 0.055) / 1.055) ** 2.4 if v > 0.04045 else v / 12.92 for v in c)

blue, screen, keys, orange, ruby = (material("Flippy blue", srgb("#2F4BD0")), material("Flippy screen", srgb("#A6F0A8"), 0.6),
                                    material("Flippy keys", srgb("#8FA6F2")), material("Flippy crown", srgb("#F08A2C")), material("Flippy gem", srgb("#E0283A")))

# body: front at y = -0.190, back at +0.167 (Blender), so its centre sits a little forward
bpy.ops.mesh.primitive_cube_add(size=1)
body = put(bpy.context.object, ref)
body.name = "Flippy body"
body.dimensions = (0.8724, 0.3575, 2.0496)
body.location = (0, (-0.1902 + 0.1673) / 2, (-0.9973 + 1.0523) / 2)
bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
bev = body.modifiers.new("Round corners", "BEVEL")
bev.width = 0.15
bev.segments = 6
body.data.materials.append(blue)

# screen
bpy.ops.mesh.primitive_cube_add(size=1)
scr = put(bpy.context.object, ref)
scr.name = "Flippy screen"
scr.dimensions = (0.6, 0.02, 0.5335)
scr.location = (0, -0.195, (0.162 + 0.6955) / 2)
scr.data.materials.append(screen)

# keypad: 3 x 4 buttons plus the nav key
for row in range(4):
    for colx in range(3):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.07, segments=16, ring_count=8)
        k = put(bpy.context.object, ref)
        k.name = f"Flippy key {row * 3 + colx + 1}"
        k.scale = (1.25, 0.35, 0.8)
        k.location = ((colx - 1) * 0.22, -0.19, -0.22 - row * 0.18)
        k.data.materials.append(keys)
bpy.ops.mesh.primitive_uv_sphere_add(radius=0.1, segments=16, ring_count=8)
nav = put(bpy.context.object, ref)
nav.name = "Flippy nav key"
nav.scale = (1.6, 0.35, 0.6)
nav.location = (0, -0.19, 0.03)
nav.data.materials.append(keys)

# crown on top: a band with five points and gems
bpy.ops.mesh.primitive_cylinder_add(radius=0.38, depth=0.14, vertices=48)
band = put(bpy.context.object, ref)
band.name = "Flippy crown band"
band.scale = (1.12, 0.48, 1)
band.location = (0, -0.01, 1.07)
band.data.materials.append(orange)
for i in range(5):
    x = (i - 2) * 0.19
    bpy.ops.mesh.primitive_cone_add(radius1=0.075, depth=0.2, vertices=12)
    p = put(bpy.context.object, ref)
    p.name = f"Flippy crown point {i + 1}"
    p.location = (x, -0.1, 1.2)
    p.data.materials.append(orange)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.032, segments=12, ring_count=6)
    g = put(bpy.context.object, ref)
    g.name = f"Flippy gem {i + 1}"
    g.location = (x, -0.185, 1.07)
    g.data.materials.append(ruby)

for o in ref.objects:
    o.hide_select = True  # so the stand-in can't be moved by accident (it isn't exported anyway)

# the example accessory: a gold halo above Flippy
bpy.ops.mesh.primitive_torus_add(major_radius=0.42, minor_radius=0.045, major_segments=64, minor_segments=12)
halo = put(bpy.context.object, acc)
halo.name = "Example halo"
halo.location = (0, 0, 1.55)
halo.rotation_euler = (math.radians(8), 0, 0)
halo.data.materials.append(material("Gold", srgb("#FFC93D"), 0.4))

# a readable note in the viewport
bpy.ops.object.text_add(location=(0.75, 0, 1.2))
note = put(bpy.context.object, ref)
note.name = "How to"
note.data.body = "Model inside the Accessory collection.\nPress Export to FIH (N panel > FIH),\nor just save (Ctrl+S)."
note.data.size = 0.09
note.rotation_euler = (math.radians(90), 0, 0)
note.hide_select = True

# front view, looking at Flippy's screen
for area in bpy.context.screen.areas if bpy.context.screen else []:
    if area.type == "VIEW_3D":
        area.spaces[0].region_3d.view_perspective = "PERSP"
bpy.context.view_layer.active_layer_collection = bpy.context.view_layer.layer_collection.children["Accessory"]
bpy.ops.wm.save_as_mainfile(filepath=out)
print("template saved:", out)
