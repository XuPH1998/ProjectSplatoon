"""Blender 5.2: deterministic, editable low-poly toy-water-gun asset.
Run: blender -b --factory-startup --python build_water_shotgun.py -- --render
The measured Unity weapon-local frame is authoritative. No gameplay assets are edited.
"""
import bpy, bmesh, json, math, sys, argparse
from pathlib import Path
from mathutils import Vector, Matrix, Quaternion

ROOT = Path(__file__).resolve().parents[1]
PALETTE = ['teal','ivory','orange','grip','deep','light','black','white']
PARTS=[]
REF=json.loads((ROOT/'Reference/shotgun-reference.json').read_text(encoding='utf-8-sig'))
vec=lambda p: Vector((p['x'],p['y'],p['z']))
ORIGIN=vec(REF['origin']); FWD=vec(REF['forward']); RIGHT=vec(REF['right']); UP=vec(REF['up'])
MIN,MAX=vec(REF['min']),vec(REF['max'])

def design_to_unity(p):
    return ORIGIN+FWD*p.x-RIGHT*p.y+UP*p.z

def unity_to_design(p):
    p=p-ORIGIN
    return Vector((p.dot(FWD),-p.dot(RIGHT),p.dot(UP)))

def collection(name):
    c=bpy.data.collections.new(name);bpy.context.scene.collection.children.link(c);return c

def move_collection(obj, coll):
    for old in list(obj.users_collection):old.objects.unlink(obj)
    coll.objects.link(obj)

def recalc(mesh):
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces[:]);bm.to_mesh(mesh);bm.free()

def finish(obj,color,bevel=0.0,segments=1,smooth=False):
    obj.data.materials.clear();obj.data.materials.append(MATERIAL)
    if bevel:
        bpy.context.view_layer.objects.active=obj
        m=obj.modifiers.new('Small molded edge','BEVEL');m.width=bevel;m.segments=segments
        m.affect='EDGES';m.limit_method='ANGLE'
        bpy.ops.object.modifier_apply(modifier=m.name)
    recalc(obj.data)
    for p in obj.data.polygons:p.use_smooth=smooth
    # Unwrap contiguous surfaces rather than introducing a UV seam on every face.
    bpy.ops.object.select_all(action='DESELECT');obj.select_set(True);bpy.context.view_layer.objects.active=obj
    bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.025,scale_to_bounds=True)
    bpy.ops.object.mode_set(mode='OBJECT')
    uv=obj.data.uv_layers.active;uv.name='ColorAtlas'
    idx=PALETTE.index(color)
    for li in uv.data:
        u,v=li.uv
        li.uv=((idx+.5)*.125+(u-.5)*.072, .925+(v-.5)*.072)
    obj['palette']=color
    move_collection(obj,EDIT);PARTS.append(obj)
    return obj

def profile(name,points,width,color,bevel=.002,segments=1,y=0):
    n=len(points)
    vertices=[(x,y+s*width/2,z) for s in (-1,1) for x,z in points]
    faces=[tuple(range(n-1,-1,-1)),tuple(range(n,2*n))]
    faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    mesh=bpy.data.meshes.new(name);mesh.from_pydata(vertices,[],faces);mesh.update()
    obj=bpy.data.objects.new(name,mesh);EDIT.objects.link(obj)
    return finish(obj,color,bevel,segments,False)

def box(name,center,dimensions,color,bevel=.002,segments=1):
    bpy.ops.mesh.primitive_cube_add(size=1,location=center);obj=bpy.context.object;obj.name=name
    obj.scale=dimensions;bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    return finish(obj,color,bevel,segments,False)

def tube(name,rings,color,segments=16,cap=True,closed=False):
    # rings are (x, halfwidth, halfheight, center_z); editable flattened capsules.
    vs=[(x,ry*math.sin(2*math.pi*j/segments),zc+rz*math.cos(2*math.pi*j/segments)) for x,ry,rz,zc in rings for j in range(segments)]
    fs=[]
    for i in range(len(rings)-1):
        for j in range(segments):
            a=i*segments+j;b=i*segments+(j+1)%segments
            fs.append((a,b,b+segments,a+segments))
    if cap:fs += [tuple(range(segments-1,-1,-1)),tuple(range((len(rings)-1)*segments,len(rings)*segments))]
    if closed:
        last=(len(rings)-1)*segments
        fs += [(last+j,last+(j+1)%segments,(j+1)%segments,j) for j in range(segments)]
    mesh=bpy.data.meshes.new(name);mesh.from_pydata(vs,[],fs);mesh.update()
    obj=bpy.data.objects.new(name,mesh);EDIT.objects.link(obj)
    finish(obj,color,0,smooth=True)
    # Keep end caps flat while cylindrical faces interpolate normals.
    if cap:
        obj.data.polygons[-1].use_smooth=False;obj.data.polygons[-2].use_smooth=False
    return obj

def cylinder(name,center,radius,depth,color,segments=16,axis='Z'):
    bpy.ops.mesh.primitive_cylinder_add(vertices=segments,radius=radius,depth=depth,location=center)
    obj=bpy.context.object;obj.name=name
    if axis=='Y':obj.rotation_euler.x=math.pi/2
    if axis=='X':obj.rotation_euler.y=math.pi/2
    bpy.ops.object.transform_apply(location=False,rotation=True,scale=True)
    finish(obj,color,min(.001,depth*.2),1,True)
    cap_axis={'X':0,'Y':1,'Z':2}[axis]
    for face in obj.data.polygons:
        if abs(face.normal[cap_axis])>.999:face.use_smooth=False
    return obj

def badge_uv(obj,rect):
    # Place a readable decal on each side; reverse the far side to avoid mirrored text.
    uv=obj.data.uv_layers.active
    xs=[v.co.x for v in obj.data.vertices];zs=[v.co.z for v in obj.data.vertices]
    x0,x1=min(xs),max(xs);z0,z1=min(zs),max(zs)
    u0,v0,u1,v1=rect
    for p in obj.data.polygons:
        if abs(p.normal.y)<.9:continue
        for li in p.loop_indices:
            co=obj.data.vertices[obj.data.loops[li].vertex_index].co
            tx=(co.x-x0)/(x1-x0);tz=(co.z-z0)/(z1-z0)
            if p.normal.y>0:tx=1-tx
            uv.data[li].uv=(u0+(u1-u0)*tx,v0+(v1-v0)*tz)

def setup_material():
    mat=bpy.data.materials.new('WaterShotgun_ToonPreview');mat.use_nodes=True
    nodes=mat.node_tree.nodes;nodes.clear();links=mat.node_tree.links
    out=nodes.new('ShaderNodeOutputMaterial');out.location=(640,60)
    emit=nodes.new('ShaderNodeEmission');emit.location=(440,60)
    texture=nodes.new('ShaderNodeTexImage');texture.image=bpy.data.images.load(str(ROOT/'Export/WaterShotgun_Albedo.png'));texture.location=(-600,180)
    diffuse=nodes.new('ShaderNodeBsdfDiffuse');diffuse.inputs['Color'].default_value=(.8,.8,.8,1);diffuse.location=(-600,-140)
    srgb=nodes.new('ShaderNodeShaderToRGB');srgb.location=(-400,-140)
    ramp=nodes.new('ShaderNodeValToRGB');ramp.location=(-190,-140)
    ramp.color_ramp.interpolation='CONSTANT'
    ramp.color_ramp.elements[0].position=.18;ramp.color_ramp.elements[0].color=(.32,.40,.46,1)
    ramp.color_ramp.elements[1].position=.48;ramp.color_ramp.elements[1].color=(1,1,1,1)
    mid=ramp.color_ramp.elements.new(.32);mid.color=(.65,.75,.78,1)
    mult=nodes.new('ShaderNodeMixRGB');mult.blend_type='MULTIPLY';mult.inputs[0].default_value=1;mult.location=(200,60)
    links.new(diffuse.outputs[0],srgb.inputs[0]);links.new(srgb.outputs[0],ramp.inputs[0])
    links.new(texture.outputs['Color'],mult.inputs[1]);links.new(ramp.outputs[0],mult.inputs[2])
    links.new(mult.outputs[0],emit.inputs[0]);links.new(emit.outputs[0],out.inputs[0])
    return mat

def build_parts():
    # Dimensions in metres in the calibrated muzzle frame: X forward, Y left, Z up.
    # Slim stock, grip neck, trigger guard and pump contact envelopes follow the reference.
    stock=profile('01 Ivory molded shoulder stock',[
        (-.993,-.058),(-.816,-.043),(-.773,-.041),(-.737,-.074),
        (-.770,-.110),(-.957,-.174),(-.990,-.184)],.035,'ivory',.003,2,y=.0205)
    profile('02 Charcoal shoulder pad',[(MIN.x,-.057),(-.992,-.055),(-.978,-.179),(-.995,MIN.z)],.0367,'grip',.0018,1,y=.021)
    profile('03 Teal stock side insert',[(-.976,-.074),(-.840,-.059),(-.814,-.064),(-.833,-.118),(-.963,-.159)],.0365,'teal',.0014,1,y=.0205)
    profile('04 Orange heel accent',[(-.978,-.166),(-.835,-.124),(-.844,-.135),(-.978,-.178)],.037,'orange',.001,1,y=.0205)
    profile('05 Original angle grip neck',[(-.819,-.042),(-.769,-.037),(-.709,-.022),(-.681,-.038),
        (-.716,-.066),(-.754,-.104),(-.784,-.107)],.030,'grip',.002,2,y=.016)
    # A real open trigger guard, formed as one closed extruded ring.
    outer=[(-.704,-.051),(-.625,-.051),(-.628,-.080),(-.644,-.098),(-.681,-.108),(-.701,-.100),(-.714,-.078)]
    inner=[(-.699,-.059),(-.634,-.059),(-.637,-.077),(-.650,-.090),(-.680,-.099),(-.695,-.092),(-.705,-.077)]
    n=len(outer);vs=[(x,y,z) for y in (.008,.020) for poly in (outer,inner) for x,z in poly];fs=[]
    for i in range(n):
        j=(i+1)%n
        fs.extend([(i,j,j+n,i+n),(i+2*n,i+3*n,j+3*n,j+2*n),(i,i+2*n,j+2*n,j),(i+n,j+n,j+3*n,i+3*n)])
    mesh=bpy.data.meshes.new('TriggerGuard');mesh.from_pydata(vs,[],fs);mesh.update()
    obj=bpy.data.objects.new('06 Open molded trigger guard',mesh);EDIT.objects.link(obj);finish(obj,'deep',.001,1)
    profile('07 Orange squeeze trigger',[(-.665,-.051),(-.655,-.053),(-.659,-.075),(-.669,-.084),(-.675,-.081),(-.666,-.069)],.010,'orange',.001,1,y=.014)
    profile('08 Main ivory pressure housing',[(-.722,-.027),(-.697,.002),(-.644,.014),(-.518,.014),
        (-.481,-.005),(-.488,-.066),(-.661,-.071),(-.699,-.057)],.029,'ivory',.003,2,y=.012)
    reservoir=tube('09 Integrated teal water reservoir',[
        (-.702,.009,.012,-.001),(-.682,.015,.027,-.002),(-.652,.015,.028,-.002),
        (-.533,.015,.026,0),(-.494,.013,.023,-.002),(-.477,.006,.010,-.004)],'teal',16)
    reservoir.location.y=.012
    cylinder('10 Dark filler seal',(-.588,.012,.022),.016,.008,'deep',16)
    cylinder('11 Orange quarter turn fill cap',(-.588,.012,.029),.015,.0094,'orange',16)
    box('12 Fill cap raised tab',(-.588,.012,.0330),(.019,.006,.0014),'ivory',.0006,1)
    # Low-relief side badge and level window share the only atlas/material.
    badge=profile('13 Printed ivory side badge',[(-.682,-.018),(-.511,-.018),(-.499,-.028),(-.504,-.057),(-.680,-.057)],.032,'ivory',.0013,1,y=.012)
    badge_uv(badge,(32/1024,1-640/1024,992/1024,1-224/1024))
    level=profile('14 Molded level scale',[(-.650,.010),(-.532,.010),(-.532,.021),(-.650,.021)],.031,'deep',.0007,1,y=.012)
    badge_uv(level,(64/1024,1-960/1024,960/1024,1-704/1024))
    barrel=tube('15 Ivory long water nozzle shroud',[
        (-.500,.012,.014,-.007),(-.477,.013,.014,-.004),(-.068,.011,.012,-.001),(-.043,.010,.011,0)],'ivory',16)
    # Original weapon has a very slight lateral slope relative to the muzzle frame.
    for v in barrel.data.vertices:v.co.y+=-.019*v.co.x
    rail=profile('16 Teal upper spine',[(-.488,.010),(-.107,.010),(-.067,.014),(-.056,.009),(-.060,.004),(-.488,.003)],.019,'teal',.001,1,y=.0045)
    lower=tube('17 Lower pressure channel',[(-.510,.008,.009,-.035),(-.128,.008,.010,-.030),(-.106,.006,.008,-.028)],'deep',12)
    for v in lower.data.vertices:v.co.y+=-.019*v.co.x
    pump=tube('18 Dark ergonomic pump contact',[
        (-.391,.010,.017,-.034),(-.383,.014,.020,-.034),(-.222,.014,.020,-.031),(-.206,.010,.017,-.031)],'grip',16)
    for v in pump.data.vertices:v.co.y+=-.019*v.co.x
    for i in range(6):
        x=-.365+i*.025
        rib=tube('19 Pump rib %02d'%i,[(x-.004,.014,.020,-.033),(x-.002,.015,.021,-.033),
            (x+.002,.015,.021,-.033),(x+.004,.014,.020,-.033)],'deep',12)
        rib.location.y=-.019*x
    collar=tube('20 Orange pump release collar',[
        (-.219,.013,.019,-.031),(-.213,.015,.021,-.031),(-.202,.015,.021,-.031),(-.197,.012,.018,-.031)],'orange',16)
    collar.location.y=.005
    bridge=profile('21 Front bridge housing',[(-.114,.001),(-.084,.008),(-.076,-.014),(-.087,-.038),(-.111,-.038)],.028,'teal',.002,1,y=.005)
    nozzle=tube('22 Orange safety nozzle',[
        (-.058,.011,.012,0),(-.045,.014,.020,0),(-.014,.014,.020,0),(MAX.x,.012,.018,0),
        (MAX.x,.0065,.009,0),(-.020,.0065,.009,0)],'orange',20,False,True)
    for face in nozzle.data.polygons:
        if abs(face.normal.x)>.99:face.use_smooth=False
    # The last ring returns into the barrel to form a visible nozzle recess.
    tube('23 Recessed dark outlet',[(-.021,.0064,.0089,0),(-.020,.0064,.0089,0)],'black',16)
    # Molded side fasteners: two paired circles, with no microscopic geometry.
    for i,(x,z) in enumerate([(-.700,-.038),(-.492,-.043)]):
        for side in (-1,1):
            cylinder('24 Housing fastener %s %s'%(i,side),(x,.012+side*.0152,z),.0047,.0018,'deep',10,'Y')

def export_game():
    export_coll=collection('EXPORT — one mesh and three calibration anchors')
    copies=[]
    for part in PARTS:
        clone=part.copy();clone.data=part.data.copy();export_coll.objects.link(clone);copies.append(clone)
    bpy.ops.object.select_all(action='DESELECT')
    for o in copies:o.select_set(True)
    bpy.context.view_layer.objects.active=copies[0];bpy.ops.object.join();game=copies[0];game.name='WaterShotgun_Mesh'
    bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
    # Keep export objects in the measured weapon-local frame. The FBX conversion
    # is validated with a separate coordinate probe, not inferred from visual orientation.
    # Measured with coordinate-probe.fbx: Blender(X,Y,Z) -> Unity(-X,Z,-Y).
    convert=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
    authored=Matrix(((FWD.x,-RIGHT.x,UP.x,ORIGIN.x),(FWD.y,-RIGHT.y,UP.y,ORIGIN.y),
                     (FWD.z,-RIGHT.z,UP.z,ORIGIN.z),(0,0,0,1)))
    game.data.transform(convert.to_4x4()@authored)
    recalc(game.data)
    bpy.context.view_layer.objects.active=game
    tri=game.modifiers.new('Final game triangles','TRIANGULATE');bpy.ops.object.modifier_apply(modifier=tri.name)
    game.data.materials.clear()
    game_mat=bpy.data.materials.new('WaterShotgun');game_mat.use_nodes=True
    bs=game_mat.node_tree.nodes.get('Principled BSDF');bs.inputs['Roughness'].default_value=.52
    tex=game_mat.node_tree.nodes.new('ShaderNodeTexImage');tex.image=MATERIAL.node_tree.nodes.get('Image Texture').image
    game_mat.node_tree.links.new(tex.outputs['Color'],bs.inputs['Base Color']);game.data.materials.append(game_mat)
    objects=[game]
    for record in REF['markers']:
        ob=bpy.data.objects.new(record['name'],None);export_coll.objects.link(ob)
        ob.location=convert@vec(record['position'])
        q=record['rotation'];u=Quaternion((q['w'],q['x'],q['y'],q['z'])).to_matrix()
        ob.rotation_mode='QUATERNION';ob.rotation_quaternion=(convert@u@convert.inverted()).to_quaternion()
        ob.empty_display_size=.025;objects.append(ob)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:o.select_set(True)
    bpy.context.view_layer.objects.active=game
    bpy.ops.export_scene.fbx(filepath=str(ROOT/'Export/WaterShotgun.fbx'),use_selection=True,
        object_types={'MESH','EMPTY'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_UNITS',bake_space_transform=True,use_mesh_modifiers=True,
        add_leaf_bones=False,bake_anim=False,path_mode='STRIP',mesh_smooth_type='OFF')
    count=len(game.data.polygons)
    game.data.calc_loop_triangles()
    degenerate=sum(t.area<1e-12 for t in game.data.loop_triangles)
    bm=bmesh.new();bm.from_mesh(game.data)
    nonmanifold=sum(not e.is_manifold for e in bm.edges);bm.free()
    verts=[p.matrix_world@v.co for p in PARTS for v in p.data.vertices]
    actual_min=Vector([min(v[i] for v in verts) for i in range(3)])
    actual_max=Vector([max(v[i] for v in verts) for i in range(3)])
    errors=[abs((actual_max[i]-actual_min[i])/(MAX[i]-MIN[i])-1) for i in range(3)]
    report={'triangles':count,'source_triangles':REF['triangles'],'parts':len(PARTS),
        'materials':1,'textures':[{'file':'WaterShotgun_Albedo.png','width':1024,'height':1024}],
        'source_design_min_m':list(MIN),'source_design_max_m':list(MAX),
        'design_min_m':list(actual_min),'design_max_m':list(actual_max),
        'size_relative_error':errors,'degenerate_triangles':degenerate,'nonmanifold_edges':nonmanifold,'blender_version':bpy.app.version_string,
        'per_part_triangles':{p.name:sum(len(f.vertices)-2 for f in p.data.polygons) for p in PARTS}}
    (ROOT/'Validation/blender-geometry.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    assert count<=4000, f'Triangle cap exceeded: {count}'
    assert degenerate==0, f'Degenerate triangles: {degenerate}'
    assert nonmanifold==0, f'Open or non-manifold component edges: {nonmanifold}'
    assert max(errors)<=.02, f'Dimensions outside 2%: {errors}'
    export_coll.hide_render=True;export_coll.hide_viewport=True
    return report

def add_reference():
    coll=collection('REFERENCE — original shotgun, not exported')
    mesh=bpy.data.meshes.new('Original_shotgun_reference')
    vertices=[vec(v) for v in REF['designVertices']]
    indices=REF['indices'];faces=[(indices[i+2],indices[i+1],indices[i]) for i in range(0,len(indices),3)]
    mesh.from_pydata(vertices,[],faces);mesh.update();recalc(mesh)
    ob=bpy.data.objects.new('Original shotgun — comparison only',mesh);coll.objects.link(ob)
    ob.display_type='WIRE';ob.hide_render=True
    for r in REF['markers']:
        mark=bpy.data.objects.new(r['name']+' reference',None);coll.objects.link(mark)
        mark.location=unity_to_design(vec(r['position']));mark.empty_display_size=.025
    coll.hide_render=True;coll.hide_viewport=True

def setup_stage():
    stage=collection('STUDIO — preview only')
    scene=bpy.context.scene
    # Shader-to-RGB requires Eevee; explicitly selected for the preview only.
    for name in ('BLENDER_EEVEE_NEXT','BLENDER_EEVEE'):
        try:scene.render.engine=name;break
        except TypeError:pass
    scene.render.resolution_x=1600;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG';scene.render.film_transparent=False
    scene.world.color=(.22,.22,.22)
    scene.world.use_nodes=True
    scene.world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.045,.06,.075,1)
    scene.world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.6
    scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
    scene.view_settings.exposure=0;scene.view_settings.gamma=1
    for name,loc,power,size in [('Key',(-.6,-1.5,2.3),120,2.0),('Fill',(.1,1.2,1.1),80,2.0),('Rim',(-.9,.8,1.4),90,1.2)]:
        data=bpy.data.lights.new(name,'AREA');data.energy=power;data.shape='DISK';data.size=size
        ob=bpy.data.objects.new(name,data);stage.objects.link(ob);ob.location=loc
        ob.rotation_euler=(Vector((-.5,0,-.08))-ob.location).to_track_quat('-Z','Y').to_euler()
    data=bpy.data.cameras.new('Watergun preview');cam=bpy.data.objects.new('Watergun preview',data);stage.objects.link(cam)
    scene.camera=cam;data.type='ORTHO';data.lens=60;data.clip_start=.001;data.clip_end=100
    return cam

def render_views(cam):
    center=(MIN+MAX)/2
    views=[('01-hero',(.8,-1.8,.70),1.13),('02-side',(0,-2,0),1.14),
           ('03-front',(2,0,0),.43),('04-top',(0,0,2),1.14),('05-rear-quarter',(-1.4,-1.8,.65),1.17)]
    for name,direction,scale in views:
        cam.location=center+Vector(direction);cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.ortho_scale=scale
        bpy.context.scene.render.filepath=str(ROOT/'Previews'/f'{name}.png');bpy.ops.render.render(write_still=True)
    cam.location=center+Vector((.8,-1.8,.70));cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.ortho_scale=1.13

def main():
    global EDIT,MATERIAL
    bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
    EDIT=collection('EDITABLE — molded components');MATERIAL=setup_material()
    build_parts();report=export_game();add_reference();cam=setup_stage()
    for im in bpy.data.images:
        if im.source=='FILE':im.pack()
    bpy.ops.object.select_all(action='DESELECT');bpy.context.view_layer.objects.active=PARTS[0]
    for area in bpy.context.screen.areas if bpy.context.screen else []:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_distance=1.5;area.spaces.active.region_3d.view_location=(MIN+MAX)/2
            area.spaces.active.shading.type='MATERIAL'
            area.spaces.active.shading.use_scene_lights=True;area.spaces.active.shading.use_scene_world=True
            area.spaces.active.overlay.show_extras=False
    if '--render' in sys.argv:render_views(cam)
    bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'WaterShotgun.blend'))
    print('WATERGUN_COMPLETE '+json.dumps(report))

if __name__=='__main__':main()
