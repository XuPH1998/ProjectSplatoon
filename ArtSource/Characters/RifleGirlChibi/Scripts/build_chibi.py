"""Build a real skinned 2.5-head RifleGirl derivative from Unity's imported source.
Run with Blender --background --factory-startup --python build_chibi.py.
Textures/UVs and four-influence skinning come from the project's actual visible outfit.
"""
import bpy, json, math, sys
from pathlib import Path
from mathutils import Vector, Matrix, Quaternion

ART=Path(__file__).resolve().parents[1]
PROJECT=ART.parents[2]
DATA=json.loads((ART/'Reference/source-character.json').read_text(encoding='utf-8-sig'))
OUT=PROJECT/'Assets/GameResource/Characters/RifleGirlChibi/Models'
OUT.mkdir(parents=True,exist_ok=True)
(ART/'Previews').mkdir(exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
def v(p):return Vector((p['x'],p['y'],p['z']))
def xyz(p):return Vector((-p.x,-p.z,p.y))
BONES={b['name']:b for b in DATA['bones']}
POS={name:v(b['position']) for name,b in BONES.items()}
def ancestors(name):
    while name in BONES:
        yield name
        name=BONES[name]['parent']
def category(name):
    chain=list(ancestors(name))
    if 'head' in chain:return 'head'
    if any(n.startswith(('upperarm','lowerarm','hand','clavicle')) for n in chain):return 'arm'
    return 'body'
def lerpknots(x,knots):
    for i in range(len(knots)-1):
        a,b=knots[i:i+2]
        if x<=b[0]:return a[1]+(x-a[0])*(b[1]-a[1])/(b[0]-a[0])
    a,b=knots[-2:];return b[1]+(x-b[0])*(b[1]-a[1])/(b[0]-a[0])
def smooth(a,b,x):
    t=max(0,min(1,(x-a)/(b-a)));return t*t*(3-2*t)

# All dimensions are authored in metres in Unity's source rest pose.
face=next(p for p in DATA['parts'] if 'Face' in p['name'])
hair=next(p for p in DATA['parts'] if 'Hair' in p['name'])
chin=min(v(p).y for p in face['vertices'])
crown=max(v(p).y for p in hair['vertices'])
head_scale=.48/(crown-chin)
floor=min(v(p).y for part in DATA['parts'] for p in part['vertices'])
shoulder=POS['upperarm_l']
knots=[(floor,0),(POS['foot_l'].y,.075),(POS['calf_l'].y,.255),(POS['thigh_l'].y,.46),(POS['spine_02'].y,.555),(shoulder.y,.665),(chin,.72),(crown,1.20)]
knots.sort()
arm_knots=[(0,0),(abs(shoulder.x),.135),(abs(POS['lowerarm_l'].x),.285),(abs(POS['hand_l'].x),.405)]
arm_knots.sort()
def deform(p,kind):
    y=lerpknots(p.y,knots)
    x=p.x*1.04;z=p.z*.94
    if kind=='head':
        # Shorter, wider lower face, with the same UV-authored eyes and mouth.
        hair_blend=smooth(chin-.32,chin,p.y)
        x=p.x*(.90*(1-hair_blend)+head_scale*1.24*hair_blend)
        z=p.z*head_scale*.92
        if p.y>=chin:y=.72+(p.y-chin)*head_scale
        # Long hair retains the original silhouette but stops above the boots.
    elif kind=='arm':
        x=math.copysign(lerpknots(abs(p.x),arm_knots),p.x)
        y=.665+(p.y-shoulder.y)*.63
        z=p.z*.87
    else:
        h=smooth(POS['neck_01'].y,chin,p.y)
        x=p.x*(1.04*(1-h)+head_scale*1.24*h)
        z=p.z*(.94*(1-h)+head_scale*.92*h)
        # Keep the shortened torso soft and flat through the upper chest.
        chest=smooth(POS['spine_02'].y,POS['spine_03'].y,p.y)*(1-smooth(shoulder.y,POS['neck_01'].y,p.y))
        z-=max(0,z-.07)*.45*chest
    return Vector((x,y,z))

armdata=bpy.data.armatures.new('RifleGirlChibi_Skeleton')
rig=bpy.data.objects.new('RifleGirlChibi',armdata);bpy.context.collection.objects.link(rig)
bpy.context.view_layer.objects.active=rig;rig.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
for name,b in BONES.items():
    bone=armdata.edit_bones.new(name);bone.head=xyz(deform(POS[name],category(name)))
    bone.tail=bone.head+Vector((0,0,.025))
for name,b in BONES.items():
    bone=armdata.edit_bones[name]
    if b['parent'] in BONES:bone.parent=armdata.edit_bones[b['parent']]
    children=[n for n,c in BONES.items() if c['parent']==name]
    candidates=[n for n in children if 'twist' not in n and (deform(POS[n],category(n))-deform(POS[name],category(name))).length>.005]
    if candidates:
        # Humanoid children before accessory bones.
        candidates.sort(key=lambda n:(n.startswith(('add_','hair','bone','Hand_')),n))
        bone.tail=xyz(deform(POS[candidates[0]],category(candidates[0])))
    bone.use_connect=False
    # Preserve the authored global bone frames, including their roll. Choosing
    # a child to aim at is ambiguous at the chest, hands and hair branching points.
    q=b['rotation'];rotation=Quaternion((q['w'],q['x'],q['y'],q['z'])).to_matrix()
    conversion=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
    matrix=(conversion@rotation@conversion.inverted()).to_4x4()
    matrix.translation=xyz(deform(POS[name],category(name)))
    length=max(.015,bone.length)
    bone.matrix=matrix;bone.length=length
bpy.ops.object.mode_set(mode='OBJECT')
rig.show_in_front=True
materials={}
def material(md):
    if md['path'] in materials:return materials[md['path']]
    name=Path(md['path']).stem
    m=bpy.data.materials.new(name);m.use_nodes=True
    nodes=m.node_tree.nodes;nodes.clear()
    out=nodes.new('ShaderNodeOutputMaterial');base=nodes.new('ShaderNodeBsdfPrincipled')
    base.inputs['Roughness'].default_value=.85
    m.node_tree.links.new(base.outputs['BSDF'],out.inputs['Surface'])
    tex=PROJECT/md['texture']
    if md['texture'] and tex.exists():
        image=bpy.data.images.load(str(tex),check_existing=True)
        n=nodes.new('ShaderNodeTexImage');n.image=image
        m.node_tree.links.new(n.outputs['Color'],base.inputs['Base Color'])
        m.node_tree.links.new(n.outputs['Color'],base.inputs['Emission Color']);base.inputs['Emission Strength'].default_value=.35
    m['unity_material']=md['path'];materials[md['path']]=m;return m

stats={'head_units':2.5,'height_without_hat':1.2,'head_height':.48,'source_chin':chin,'source_crown':crown,'head_scale':head_scale,'bones':len(BONES),'parts':[],'unweighted_vertices':0,'max_influences':0,'max_weight_sum_error':0}
for part in DATA['parts']:
    coords=[];weights=[]
    for i,p in enumerate(part['vertices']):
        bw=part['weights'][i]
        ws=[(part['bones'][bw.get('boneIndex'+str(j),bw.get('m_BoneIndex'+str(j),0))],bw.get('weight'+str(j),bw.get('m_Weight'+str(j),0))) for j in range(4)]
        ws=[(n,w) for n,w in ws if w>1e-6];total=sum(w for n,w in ws)
        if not ws:raise RuntimeError('Unweighted vertex: '+part['name']+' '+str(i))
        ws=[(n,w/total) for n,w in ws]
        point=sum((deform(v(p),category(n))*w for n,w in ws),Vector())
        coords.append(xyz(point));weights.append(ws)
        stats['max_influences']=max(stats['max_influences'],len(ws))
        stats['max_weight_sum_error']=max(stats['max_weight_sum_error'],abs(1-sum(w for n,w in ws)))
    faces=[];slots=[]
    for mi,sub in enumerate(part['submeshes']):
        a=sub['indices']
        for j in range(0,len(a),3):faces.append((a[j],a[j+2],a[j+1]));slots.append(mi)
    mesh=bpy.data.meshes.new(part['name']);mesh.from_pydata(coords,[],faces);mesh.update()
    obj=bpy.data.objects.new(part['name'],mesh);bpy.context.collection.objects.link(obj)
    for md in part['materials']:mesh.materials.append(material(md))
    uv=mesh.uv_layers.new(name='UVMap')
    for poly,mi in zip(mesh.polygons,slots):
        poly.material_index=mi;poly.use_smooth=True
        for li in poly.loop_indices:
            source_uv=part['uv'][mesh.loops[li].vertex_index];uv.data[li].uv=(source_uv['x'],source_uv['y'])
    groups={name:obj.vertex_groups.new(name=name) for name in part['bones']}
    for i,ws in enumerate(weights):
        for name,w in ws:groups[name].add([i],w,'REPLACE')
    obj.parent=rig;mod=obj.modifiers.new('RifleGirlChibi skin','ARMATURE');mod.object=rig
    stats['parts'].append({'name':part['name'],'vertices':len(coords),'triangles':len(faces),'material_count':len(mesh.materials)})

# Bake a true T-pose for Humanoid retargeting. The source's authored A-pose
# cannot be supplied as a HumanDescription T-pose without lowering aim poses.
for side,sign in [('l',1),('r',-1)]:
    for name,child in [('upperarm_'+side,'lowerarm_'+side),('lowerarm_'+side,'hand_'+side)]:
        bone=rig.pose.bones[name];target=rig.pose.bones[child]
        direction=(target.head-bone.head).normalized()
        delta=direction.rotation_difference(Vector((sign,0,0))).to_matrix().to_4x4()
        pivot=bone.head.copy()
        bone.matrix=Matrix.Translation(pivot)@delta@Matrix.Translation(-pivot)@bone.matrix
        bpy.context.view_layer.update()
for obj in [o for o in bpy.data.objects if o.type=='MESH']:
    bpy.context.view_layer.objects.active=obj
    bpy.ops.object.modifier_apply(modifier='RifleGirlChibi skin')
bpy.context.view_layer.objects.active=rig
bpy.ops.object.mode_set(mode='POSE');bpy.ops.pose.armature_apply(selected=False);bpy.ops.object.mode_set(mode='OBJECT')
for obj in [o for o in bpy.data.objects if o.type=='MESH']:
    mod=obj.modifiers.new('RifleGirlChibi skin','ARMATURE');mod.object=rig
conversion=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
restframes=[]
for bone in rig.data.bones:
    q=(conversion.inverted()@bone.matrix_local.to_3x3()@conversion).to_quaternion()
    restframes.append({'name':bone.name,'rotation':{'x':q.x,'y':q.y,'z':q.z,'w':q.w}})
(ART/'Reference/chibi-rest-frames.json').write_text(json.dumps({'bones':restframes},indent=2),encoding='utf-8')
rig['proportion']='2.5 heads measured crown-to-chin; hat excluded'
rig['source']='ProjectSplatoon RifleGirlVisual; original visible outfit, UVs and skin weights'
rig['animation']='Unity Humanoid; use a new Avatar generated from this skeleton'
rig['body_height_m']=1.2
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=str(OUT/'RifleGirlChibi.fbx'),use_selection=True,object_types={'ARMATURE','MESH'},add_leaf_bones=False,bake_anim=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',mesh_smooth_type='OFF',use_mesh_modifiers=True,path_mode='AUTO')
# Make the editable .blend self-contained, without duplicating texture images on disk.
bpy.ops.file.pack_all()
scene=bpy.context.scene;scene.render.engine='CYCLES';scene.cycles.samples=32
scene.world=bpy.data.worlds.new('Studio');scene.world.use_nodes=True
scene.world.node_tree.nodes['Background'].inputs[0].default_value=(.30,.34,.43,1)
scene.world.node_tree.nodes['Background'].inputs[1].default_value=.8
scene.view_settings.view_transform='Standard'
scene.render.resolution_x=1000;scene.render.resolution_y=1100;scene.render.resolution_percentage=100
for name,loc,power,size in [('Key',(-3,-4,5),350,4),('Fill',(3,-2,3),180,3),('Rim',(1,3,4),280,3)]:
    light=bpy.data.lights.new(name,'AREA');light.energy=power;light.shape='DISK';light.size=size
    ob=bpy.data.objects.new(name,light);bpy.context.collection.objects.link(ob);ob.location=loc;ob.rotation_euler=(Vector((0,0,.65))-ob.location).to_track_quat('-Z','Y').to_euler()
camera=bpy.data.cameras.new('Review Camera');cam=bpy.data.objects.new('Review Camera',camera);bpy.context.collection.objects.link(cam);scene.camera=cam
camera.type='ORTHO';camera.ortho_scale=1.55
bpy.context.view_layer.objects.active=rig;bpy.ops.object.select_all(action='DESELECT');rig.select_set(True)
for space in [a.spaces.active for s in bpy.data.screens for a in s.areas if a.type=='VIEW_3D']:
    space.region_3d.view_distance=2;space.region_3d.view_location=(0,0,.65);space.shading.type='MATERIAL'
cam.location=(0,-4,.7);cam.rotation_euler=(Vector((0,0,.65))-cam.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.wm.save_as_mainfile(filepath=str(ART/'RifleGirlChibi.blend'))
(ART/'Validation/blender-validation.json').write_text(json.dumps(stats,indent=2),encoding='utf-8')
if '--render' in sys.argv:
    for name,loc in [('front',(0,-4,.7)),('three-quarter',(-2.8,-4,1.5)),('back',(0,4,.75))]:
        cam.location=loc;cam.rotation_euler=(Vector((0,0,.64))-cam.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath=str(ART/'Previews'/('blender-'+name+'.png'));bpy.ops.render.render(write_still=True)
print('CHIBI_BUILD_PASS',json.dumps(stats))
