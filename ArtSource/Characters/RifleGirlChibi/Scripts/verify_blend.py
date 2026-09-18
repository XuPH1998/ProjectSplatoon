"""Measure the delivered .blend and render its actual, final T-pose."""
import bpy,json
from pathlib import Path
from mathutils import Vector

ART=Path(__file__).resolve().parents[1]
bpy.ops.wm.open_mainfile(filepath=str(ART/'RifleGirlChibi.blend'))
meshes=[o for o in bpy.data.objects if o.type=='MESH']
points=lambda o:[o.matrix_world@v.co for v in o.data.vertices]
floor=min(p.z for o in meshes for p in points(o))
chin=min(p.z for p in points(bpy.data.objects['Face_01Re']))
crown=max(p.z for p in points(bpy.data.objects['Rifle_Hair']))
ratio=(crown-floor)/(crown-chin)
assert abs(ratio-2.5)<.01,(floor,chin,crown,ratio)
rig=next(o for o in bpy.data.objects if o.type=='ARMATURE')
for side in ('l','r'):
    ys=[rig.data.bones[n+'_'+side].head_local.z for n in ('upperarm','lowerarm','hand')]
    assert max(ys)-min(ys)<.001,(side,ys)
bad=0;max_error=0
for o in meshes:
    for vert in o.data.vertices:
        influences=[g for g in vert.groups if g.weight>1e-6]
        if not influences or len(influences)>4:bad+=1
        assert all(o.vertex_groups[g.group].name in rig.data.bones for g in influences)
        max_error=max(max_error,abs(sum(g.weight for g in influences)-1))
assert bad==0 and max_error<1e-5
path=ART/'Validation/blender-validation.json'
data=json.loads(path.read_text(encoding='utf-8'))
data.update(measured_head_units=ratio,measured_body_height=crown-floor,measured_head_height=crown-chin,t_pose_verified=True,delivered_blend_verified=True,delivered_max_weight_sum_error=max_error)
path.write_text(json.dumps(data,indent=2),encoding='utf-8')
scene=bpy.context.scene;cam=scene.camera
for name,loc in [('front',(0,-4,.7)),('three-quarter',(-2.8,-4,1.5)),('back',(0,4,.75))]:
    cam.location=loc;cam.rotation_euler=(Vector((0,0,.64))-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath=str(ART/'Previews'/('blender-'+name+'.png'));bpy.ops.render.render(write_still=True)
print('DELIVERED_BLEND_PASS',ratio,len(rig.data.bones),max_error)
