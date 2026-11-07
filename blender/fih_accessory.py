"""Body Accessories' Blender side. The mod starts Blender with this script:
  blender <your.blend> --python fih_accessory.py -- --out <the mod's accessories folder> --name <accessory name>
It adds an "FIH" tab to the 3D view's sidebar (N): the accessory's name, "Export to FIH", and "Export on save".
Export writes everything in the "Accessory" collection (modifiers applied) to <out>/<name>.glb, through a temp
file, so the game never reads half a file. The game picks the new file up within a second."""
import json
import os
import shutil
import sys
import bpy
from bpy.app.handlers import persistent
from mathutils import Matrix

def arg(flag, default=""):
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    return a[a.index(flag) + 1] if flag in a and a.index(flag) + 1 < len(a) else default

OUT = arg("--out")
NAME = arg("--name", "my_accessory")
FLIPPY = arg("--flippy")  # the real plain Flippy the mod copied out of the game (flippy.obj), if it could
REF = "Flippy (reference, not exported)"

def accessory_objects():
    col = bpy.data.collections.get("Accessory")
    if col is None:
        return []
    return [o for o in col.all_objects if o.type in {"MESH", "CURVE", "SURFACE", "META", "FONT"}]

def has_texture(o):
    for slot in o.material_slots:
        m = slot.material
        if m and m.use_nodes and any(n.type == "TEX_IMAGE" and n.image for n in m.node_tree.nodes):
            return True
    return False

def unwrap(o):
    """Smart UV Project on one mesh object (what you'd do by hand: Tab, A, U, Smart UV Project)."""
    if o.type != "MESH":
        return False
    view = bpy.context.view_layer
    keep_active, keep_sel = view.objects.active, [x for x in bpy.context.selected_objects]
    for x in view.objects:
        x.select_set(False)
    o.select_set(True)
    view.objects.active = o
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=1.15192, island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")
    o.select_set(False)
    for x in keep_sel:
        x.select_set(True)
    view.objects.active = keep_active
    return True

def add_texture(o, path):
    """Puts a texture on an object: unwraps it if it has no UVs, and makes a material with the image as its colour."""
    img = bpy.data.images.load(path, check_existing=True)
    video = img.source == "MOVIE"
    if not video:
        img.pack()  # inside the .blend, so it never goes missing (videos can't be packed: they stay linked)
    if o.type == "MESH" and len(o.data.uv_layers) == 0:
        unwrap(o)
    m = bpy.data.materials.new(img.name.rsplit(".", 1)[0])
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.location = (bsdf.location.x - 320, bsdf.location.y)
    if video:
        # play it along the timeline from the scene's Start frame, so Blender shows what the game will
        iu = tex.image_user
        iu.frame_duration = max(1, img.frame_duration)
        iu.frame_start = bpy.context.scene.frame_start
        iu.frame_offset = 0
        iu.use_auto_refresh = True
        iu.use_cyclic = True
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    if img.alpha_mode != "NONE":
        nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
    o.data.materials.clear()
    o.data.materials.append(m)
    return m

MAX_FRAMES = 3000  # 2 minutes at 25 fps: plenty for a looping accessory

def record_animation(scene, pairs):
    """Every frame from the scene's Start to End: where each exported object is, relative to the Start frame (the pose
    the .glb is exported in), in the game's axes. Only objects that move are kept."""
    f0, f1 = scene.frame_start, min(scene.frame_end, scene.frame_start + MAX_FRAMES - 1)
    if f1 <= f0:
        return {}
    flip = Matrix.Diagonal((1.0, -1.0, 1.0, 1.0))  # Blender -> game: (x, y, z) -> (x, -y, z), its own inverse
    base = {n: o.matrix_world.inverted_safe() for n, o in pairs}
    tracks = {n: [] for n, _ in pairs}
    for f in range(f0, f1 + 1):
        scene.frame_set(f)
        for n, o in pairs:
            m = flip @ o.matrix_world @ base[n] @ flip
            tracks[n].append([round(m[r][c], 5) for r in range(3) for c in range(4)])
    scene.frame_set(f0)
    return {n: t for n, t in tracks.items() if any(abs(a - b) > 1e-5 for row in t for a, b in zip(row, t[0]))}

def collect_videos(objs, scene, folder, name):
    """Video textures (an Image Texture node holding a movie): the video file is copied next to the .glb, with the
    part of it the timeline shows (Blender plays one movie frame per scene frame)."""
    fps = scene.render.fps / scene.render.fps_base
    videos, seen = [], set()
    for o in objs:
        for slot in o.material_slots:
            m = slot.material
            if not m or not m.use_nodes or m.name in seen:
                continue
            for node in m.node_tree.nodes:
                img = node.image if node.type == "TEX_IMAGE" else None
                if img is None or img.source != "MOVIE":
                    continue
                src = bpy.path.abspath(img.filepath)
                if not os.path.exists(src):
                    print("[FIH] video not found:", src)
                    continue
                seen.add(m.name)
                safe = "".join(c if c.isalnum() or c in "-_" else "_" for c in m.name)
                file = f"{name}.{safe}{os.path.splitext(src)[1].lower()}"
                dst = os.path.join(folder, file)
                if not os.path.exists(dst) or os.path.getmtime(dst) < os.path.getmtime(src) or os.path.getsize(dst) != os.path.getsize(src):
                    shutil.copyfile(src, dst)
                movie_fps = float(getattr(img, "fps", 0) or 0) or fps
                iu = node.image_user
                first = scene.frame_start - iu.frame_start + 1 + iu.frame_offset  # the movie frame on the Start frame
                videos.append({"material": m.name, "file": file, "start": max(0, first - 1) / movie_fps,
                               "rate": fps / movie_fps})
                break
    return videos

def write_anim(folder, name, scene, tracks, videos):
    path = os.path.join(folder, name + ".anim.json")
    if not tracks and not videos:
        if os.path.exists(path):
            os.remove(path)  # nothing moves any more
        return 0
    frames = len(next(iter(tracks.values()))) if tracks else scene.frame_end - scene.frame_start + 1
    data = {"fps": scene.render.fps / scene.render.fps_base, "frames": frames, "tracks": tracks, "videos": videos}
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(data, f, separators=(",", ":"))
    os.replace(path + ".tmp", path)
    return frames

def export(report=None):
    scene = bpy.context.scene
    folder = scene.fih_out or OUT
    name = (scene.fih_name or NAME).strip() or "my_accessory"
    objs = accessory_objects()
    if not folder:
        return "No output folder: start Blender from the game (the mod's open key)."
    if not objs:
        return "Nothing to export: put your models in the 'Accessory' collection."
    os.makedirs(folder, exist_ok=True)
    final = os.path.join(folder, name + ".glb")
    temp = os.path.join(folder, name + ".tmp.glb")
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    keep_frame = scene.frame_current
    scene.frame_set(scene.frame_start)  # the model goes out as it is on the Start frame; the animation is relative to it
    for o in objs:
        if o.type == "MESH" and has_texture(o) and len(o.data.uv_layers) == 0:
            unwrap(o)  # a texture needs UVs: unwrap it the way Smart UV Project would
    keep_sel = [o for o in bpy.context.selected_objects]
    keep_active = bpy.context.view_layer.objects.active
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    # glTF only exports meshes: text, curves and surfaces go out as temporary mesh copies (modifiers and bevels
    # included), which are deleted again right after
    temps = []
    pairs = []  # (name in the .glb, the object whose movement it follows)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for o in objs:
        if not o.visible_get():
            continue
        if o.type == "MESH":
            o.select_set(True)
            pairs.append((o.name, o))
            continue
        mesh = bpy.data.meshes.new_from_object(o.evaluated_get(depsgraph), preserve_all_data_layers=True, depsgraph=depsgraph)
        t = bpy.data.objects.new(o.name + " (as mesh)", mesh)
        t.matrix_world = o.matrix_world.copy()
        bpy.context.scene.collection.objects.link(t)
        t.select_set(True)
        temps.append(t)
        pairs.append((t.name, o))
    props = bpy.ops.export_scene.gltf.get_rna_type().properties.keys()
    opts = dict(filepath=temp, export_format="GLB", use_selection=True, export_apply=True, export_yup=True,
                export_materials="EXPORT", export_texcoords=True, export_normals=True, export_animations=False)
    for k in ("export_colors", "export_vertex_color"):
        if k in props:
            opts[k] = True if k == "export_colors" else "MATERIAL"
    opts = {k: v for k, v in opts.items() if k in props or k == "filepath"}
    try:
        bpy.ops.export_scene.gltf(**opts)
        # the animation and videos first, then the .glb: the game reloads when the .glb changes, so both are ready
        tracks = record_animation(scene, pairs)
        videos = collect_videos(objs, scene, folder, name)
        frames = write_anim(folder, name, scene, tracks, videos)
        os.replace(temp, final)
    finally:
        for t in temps:
            mesh = t.data
            bpy.data.objects.remove(t, do_unlink=True)
            bpy.data.meshes.remove(mesh)
        for o in bpy.context.view_layer.objects:
            o.select_set(o in keep_sel)
        bpy.context.view_layer.objects.active = keep_active
        scene.frame_set(keep_frame)
    extra = ""
    if tracks:
        extra += f", {len(tracks)} moving over {frames} frames"
    if videos:
        extra += f", {len(videos)} video(s)"
    return f"Exported {len(objs)} object(s){extra} to {final}"

class FIH_OT_export(bpy.types.Operator):
    bl_idname = "fih.export_accessory"
    bl_label = "Export to FIH"
    bl_description = "Send the Accessory collection to Flipping is Hard"

    def execute(self, context):
        msg = export()
        self.report({"WARNING"} if msg.startswith(("No ", "Nothing")) else {"INFO"}, msg)
        return {"FINISHED"}

class FIH_OT_texture(bpy.types.Operator):
    bl_idname = "fih.add_texture"
    bl_label = "Add texture to selected"
    bl_description = "Pick a picture (PNG, JPG) or a video (MP4, MOV, WEBM): the selected objects get unwrapped and textured, ready to export"
    filepath: bpy.props.StringProperty(subtype="FILE_PATH")
    filter_glob: bpy.props.StringProperty(default="*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.mov;*.webm;*.mkv;*.avi", options={"HIDDEN"})

    def invoke(self, context, event):
        if not [o for o in context.selected_objects if o.type in {"MESH", "FONT", "CURVE", "SURFACE"}]:
            self.report({"WARNING"}, "Select a model first")
            return {"CANCELLED"}
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        done = 0
        for o in context.selected_objects:
            if o.type in {"MESH", "FONT", "CURVE", "SURFACE"}:
                add_texture(o, self.filepath)
                done += 1
        self.report({"INFO"}, f"Texture on {done} object(s). Save (Ctrl+S) to see it on Flippy.")
        return {"FINISHED"}

class FIH_PT_panel(bpy.types.Panel):
    bl_label = "Flipping is Hard"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "FIH"

    def draw(self, context):
        s = context.scene
        col = self.layout.column(align=True)
        col.prop(s, "fih_name", text="Name")
        col.separator()
        col.operator("fih.export_accessory", icon="EXPORT")
        col.operator("fih.add_texture", icon="IMAGE_DATA")
        col.prop(s, "fih_on_save")
        col.separator()
        n = len(accessory_objects())
        col.label(text=f"{n} object(s) in 'Accessory'", icon="OUTLINER_COLLECTION")
        col.label(text="Flippy himself is never exported.", icon="INFO")
        col.operator("fih.reload_flippy", icon="FILE_REFRESH")

@persistent
def on_save(_):
    if bpy.context.scene.fih_on_save:
        print("[FIH]", export())

def use_game_flippy(force=False):
    """Swaps the stand-in for the real Flippy from the game (once per .blend, or again with force)."""
    if not FLIPPY or not os.path.exists(FLIPPY):
        return "No game model yet: the stand-in stays."
    ref = bpy.data.collections.get(REF)
    if ref is None:
        ref = bpy.data.collections.new(REF)
        bpy.context.scene.collection.children.link(ref)
    game = [o for o in ref.objects if o.name.startswith("GameFlippy")]
    stamp = str(os.path.getmtime(FLIPPY))
    if game and not force and bpy.context.scene.get("fih_flippy_stamp") == stamp:
        return "Game Flippy already here."
    bpy.context.scene["fih_flippy_stamp"] = stamp  # a newer copy from the game replaces the old one
    for o in game:
        bpy.data.objects.remove(o, do_unlink=True)
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=FLIPPY, forward_axis="Y", up_axis="Z")
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        for c in o.users_collection:
            c.objects.unlink(o)
        ref.objects.link(o)
        o.hide_select = True
    for o in ref.objects:
        if o.name.startswith("Flippy ") and not o.name.startswith("GameFlippy"):
            o.hide_set(True)
            o.hide_render = True
    return f"Loaded the real Flippy from your game ({len(new)} parts)."

class FIH_OT_flippy(bpy.types.Operator):
    bl_idname = "fih.reload_flippy"
    bl_label = "Reload game Flippy"
    bl_description = "Load the plain Flippy the mod copied from your game again"

    def execute(self, context):
        self.report({"INFO"}, use_game_flippy(force=True))
        return {"FINISHED"}

@persistent
def on_load(_):
    s = bpy.context.scene
    if OUT:
        s.fih_out = OUT
    if NAME and not s.fih_name:
        s.fih_name = NAME
    try:
        print("[FIH]", use_game_flippy())
    except Exception as e:
        print("[FIH] couldn't load the game Flippy:", e)

def register():
    bpy.types.Scene.fih_out = bpy.props.StringProperty(name="Folder", subtype="DIR_PATH")
    bpy.types.Scene.fih_name = bpy.props.StringProperty(name="Name", default=NAME)
    bpy.types.Scene.fih_on_save = bpy.props.BoolProperty(name="Export on save (Ctrl+S)", default=True)
    bpy.utils.register_class(FIH_OT_export)
    bpy.utils.register_class(FIH_OT_flippy)
    bpy.utils.register_class(FIH_OT_texture)
    bpy.utils.register_class(FIH_PT_panel)
    bpy.app.handlers.save_post.append(on_save)
    bpy.app.handlers.load_post.append(on_load)
    on_load(None)

register()

if "--export-now" in sys.argv:  # for testing: export and quit
    print("[FIH]", export())
