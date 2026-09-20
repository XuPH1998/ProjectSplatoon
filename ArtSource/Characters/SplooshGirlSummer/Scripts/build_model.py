"""Reproducible original-proportion A-pose to Humanoid T-pose conversion.

Run in Blender --background --factory-startup --disable-autoexec --python.
Every shape key receives the same weighted bind-pose change as the base mesh.
"""
import bpy
import json
from pathlib import Path
from mathutils import Vector, Matrix

ART = Path(__file__).resolve().parents[1]
PROJECT = ART.parents[2]
SOURCE = ART / 'Source/Avatar_Female_Size01_SummerCuteness_UI/Avatar_Female_Size01_SummerCuteness_UI.fbx'
OUTPUT = PROJECT / 'Assets/GameResource/Characters/SplooshGirl/Models/SummerCuteness.fbx'
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.fbx(filepath=str(SOURCE), use_anim=False)
rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']

# Keep all original deform bones and clothing attachments. The original skeleton
# has no animation tracks; the game drives its mapped human bones.
for side, sign in [('L', 1), ('R', -1)]:
    upper = rig.pose.bones[f'Bip001 {side} UpperArm']
    lower = rig.pose.bones[f'Bip001 {side} Forearm']
    direction = (lower.head - upper.head).normalized()
    delta = direction.rotation_difference(Vector((sign, 0, 0))).to_matrix().to_4x4()
    origin = upper.head.copy()
    upper.matrix = Matrix.Translation(origin) @ delta @ Matrix.Translation(-origin) @ upper.matrix
bpy.context.view_layer.update()
deform = {b.name: rig.pose.bones[b.name].matrix @ b.matrix_local.inverted() for b in rig.data.bones}
unweighted = 0
for obj in meshes:
    into_rig = rig.matrix_world.inverted() @ obj.matrix_world
    out_of_rig = into_rig.inverted()
    matrices = []
    for vertex in obj.data.vertices:
        weights = [(obj.vertex_groups[g.group].name, g.weight) for g in vertex.groups if g.weight > 1e-7]
        total = sum(w for _, w in weights)
        if total < 1e-6:
            unweighted += 1
            raise RuntimeError(f'Unweighted vertex: {obj.name}/{vertex.index}')
        weighted = Matrix(((0, 0, 0, 0),) * 4)
        for name, weight in weights:
            weighted += deform[name] * (weight / total)
        matrices.append(out_of_rig @ weighted @ into_rig)
    normals = [(matrices[loop.vertex_index].to_3x3().inverted().transposed() @ normal.vector).normalized()
               for loop, normal in zip(obj.data.loops, obj.data.corner_normals)]
    if obj.data.shape_keys:
        for key in obj.data.shape_keys.key_blocks:
            for vertex, matrix in zip(key.data, matrices):
                vertex.co = matrix @ vertex.co
    else:
        for vertex, matrix in zip(obj.data.vertices, matrices):
            vertex.co = matrix @ vertex.co
    obj.data.update()
    obj.data.normals_split_custom_set(normals)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.select_all(action='DESELECT')
rig.select_set(True)
bpy.ops.object.mode_set(mode='POSE')
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode='OBJECT')

# Place the sole at zero without altering the 1.500 m source proportions.
floor = min((o.matrix_world @ v.co).z for o in meshes for v in o.data.vertices)
for obj in bpy.context.scene.objects:
    if obj.parent is None:
        obj.location.z -= floor
bpy.context.view_layer.update()
points = [o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
report = dict(height=max(v.z for v in points)-min(v.z for v in points),
              floor_offset=-floor, bones=len(rig.data.bones),
              triangles=sum(len(p.vertices)-2 for o in meshes for p in o.data.polygons),
              unweighted_vertices=unweighted, source=str(SOURCE.relative_to(PROJECT)),
              meshes=[dict(name=o.name,vertices=len(o.data.vertices),
                           materials=[s.material.name if s.material else None for s in o.material_slots]) for o in meshes])
ART.joinpath('model-report.json').write_text(json.dumps(report, indent=2)+'\n')
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(ART / 'SummerCuteness.blend'))
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=str(OUTPUT), use_selection=True, object_types={'ARMATURE','MESH'},
    axis_forward='-Z', axis_up='Y', add_leaf_bones=False, bake_anim=False,
    use_armature_deform_only=False, mesh_smooth_type='OFF', use_mesh_modifiers=False,
    path_mode='STRIP', apply_scale_options='FBX_SCALE_ALL')
print('SUMMER_MODEL',json.dumps(report))
