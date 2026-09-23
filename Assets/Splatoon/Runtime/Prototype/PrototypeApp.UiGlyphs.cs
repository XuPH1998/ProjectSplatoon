using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        void UiCircle(Vector2 center,float radius,Color color) => GUI.DrawTexture(new Rect(center.x-radius,center.y-radius,radius*2,radius*2),Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,color,0,radius);
        void UiLine(Vector2 from,Vector2 to,Color color,float thickness=2)
        {
            var matrix=GUI.matrix;
            GUI.matrix=matrix*Matrix4x4.TRS(from,Quaternion.Euler(0,0,Mathf.Atan2(to.y-from.y,to.x-from.x)*Mathf.Rad2Deg),Vector3.one);
            Panel(new Rect(0,-thickness*.5f,Vector2.Distance(from,to),thickness),color);GUI.matrix=matrix;
        }
        void UiArc(Vector2 center,float radius,float fraction,Color color,float thickness=2,float start=-90)
        {
            for(int i=0;i<64*fraction;i++)
            {
                float a=(start+i*360/64f)*Mathf.Deg2Rad,b=(start+Mathf.Min(i+1,64*fraction)*360/64f)*Mathf.Deg2Rad;
                UiLine(center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,center+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,color,thickness);
            }
        }
        // Small vector glyphs stay crisp at every HUD scale and share the reference's silhouette language.
        void UiGlyph(Rect rect,int id,Color color,bool special=false,bool hud=false)
        {
            if(_uiIcons!=null&&id>=1&&id<100&&!(hud&&id==1)){UiAtlas(rect,special?12+id:id-1,color);return;}
            var matrix=GUI.matrix;GUI.matrix=matrix*Matrix4x4.TRS(new Vector3(rect.x,rect.y,0),Quaternion.identity,new Vector3(rect.width/32,rect.height/32,1));
            try
            {
                if(special)
                {
                    if(id==1){for(int i=0;i<3;i++){UiLine(new Vector2(6+i*8,8),new Vector2(6+i*8,25),color,5);UiCircle(new Vector2(6+i*8,7),2.5f,color);}}
                    else if(id==2||id==4){for(int i=0;i<3;i++)UiArc(new Vector2(16,15),5+i*4,.73f,color,3,i*55);}
                    else if(id==3){UiLine(new Vector2(16,5),new Vector2(16,27),color,3);UiArc(new Vector2(16,20),9,.5f,color,3,180);UiArc(new Vector2(16,20),14,.5f,color,2,180);}
                    else{UiLine(new Vector2(5,20),new Vector2(26,14),color,9);UiLine(new Vector2(16,15),new Vector2(13,5),color,5);UiLine(new Vector2(24,15),new Vector2(29,7),color,3);}
                    return;
                }
                switch(id)
                {
                    case 0: // room / people
                        UiCircle(new Vector2(12,8),4,color);UiCircle(new Vector2(23,10),3,color);
                        UiLine(new Vector2(6,25),new Vector2(9,18),color,7);UiLine(new Vector2(9,18),new Vector2(17,18),color,7);UiLine(new Vector2(17,18),new Vector2(20,25),color,7);UiLine(new Vector2(24,18),new Vector2(28,25),color,5);break;
                    case 1:
                        if(hud){UiCircle(new Vector2(16,20),10,Color.white);UiCircle(new Vector2(16,20),8,new Color(1,.30f,.65f));UiLine(new Vector2(13,10),new Vector2(19,7),Color.white,4);UiLine(new Vector2(20,7),new Vector2(25,5),Color.white);UiLine(new Vector2(25,5),new Vector2(28,13),Color.white);UiCircle(new Vector2(13,17),2,Color.white);}
                        else {UiLine(new Vector2(16,5),new Vector2(4,27),color,3);UiLine(new Vector2(4,27),new Vector2(28,27),color,3);UiLine(new Vector2(28,27),new Vector2(16,5),color,3);UiLine(new Vector2(16,12),new Vector2(16,24),color);UiCircle(new Vector2(16,4),3,color);}break;
                    case 2:Panel(new Rect(7,4,18,5),color);UiLine(new Vector2(10,9),new Vector2(20,23),color,7);UiLine(new Vector2(22,9),new Vector2(12,23),color,7);UiCircle(new Vector2(16,26),6,color);break;
                    case 3:UiArc(new Vector2(16,18),10,1,color,3);UiLine(new Vector2(15,8),new Vector2(18,3),color,3);UiLine(new Vector2(19,3),new Vector2(25,7),color);break;
                    case 4:UiLine(new Vector2(6,24),new Vector2(26,24),color,8);UiArc(new Vector2(16,17),8,.5f,color,3,180);UiLine(new Vector2(12,8),new Vector2(23,8),color,3);break;
                    case 5:UiCircle(new Vector2(16,13),8,color);Panel(new Rect(9,12,14,10),color);UiLine(new Vector2(10,23),new Vector2(5,28),color,3);UiLine(new Vector2(22,23),new Vector2(27,28),color,3);UiLine(new Vector2(16,5),new Vector2(20,2),color);UiCircle(new Vector2(16,12),2,UiSurface);break;
                    case 6:UiArc(new Vector2(16,18),9,1,color,3);Panel(new Rect(12,4,8,5),color);Panel(new Rect(10,12,12,13),color);break;
                    case 7:UiLine(new Vector2(9,22),new Vector2(23,9),color,9);UiLine(new Vector2(7,22),new Vector2(3,28),color,4);UiLine(new Vector2(23,9),new Vector2(27,4),color,3);UiCircle(new Vector2(21,11),2,UiSurface);break;
                    case 8:UiLine(new Vector2(5,26),new Vector2(27,26),color,3);for(int i=0;i<3;i++)UiLine(new Vector2(8+i*8,25),new Vector2(9+i*7,11),color,3);UiCircle(new Vector2(16,5),2,color);break;
                    case 9:UiArc(new Vector2(16,16),10,1,color,3);UiLine(new Vector2(16,1),new Vector2(16,9),color);UiLine(new Vector2(16,23),new Vector2(16,31),color);UiLine(new Vector2(1,16),new Vector2(9,16),color);UiLine(new Vector2(23,16),new Vector2(31,16),color);break;
                    case 10:UiCircle(new Vector2(9,19),7,color);UiCircle(new Vector2(16,10),7,color);UiCircle(new Vector2(24,18),6,color);Panel(new Rect(9,19,15,6),color);break;
                    case 11:UiLine(new Vector2(7,27),new Vector2(25,9),color,6);UiLine(new Vector2(25,9),new Vector2(28,3),color,2);UiLine(new Vector2(10,13),new Vector2(18,21),UiSurface,2);break;
                    case 12:UiCircle(new Vector2(16,23),7,color);UiLine(new Vector2(16,16),new Vector2(16,10),color,3);UiLine(new Vector2(9,10),new Vector2(23,10),color,3);for(int i=0;i<4;i++)UiCircle(new Vector2(5+i*7,4),1.5f,color);break;
                    case 13:for(int i=0;i<4;i++){UiLine(new Vector2(5+i*7,4),new Vector2(5+i*7,28),color,2);UiLine(new Vector2(5,4+i*8),new Vector2(26,4+i*8),color,2);}break;
                    case 100:for(int i=0;i<10;i++){float a=i*Mathf.PI/5;UiLine(new Vector2(16,16),new Vector2(16+Mathf.Cos(a)*(i%2==0?14:10),16+Mathf.Sin(a)*(i%2==0?14:10)),color,4);}break;
                    case 101:for(int i=0;i<3;i++){UiLine(new Vector2(7+i*9,9),new Vector2(7+i*9,27),color,4);UiCircle(new Vector2(7+i*9,7),2,color);}break;
                    case 102:for(int y=0;y<3;y++)for(int x=0;x<3;x++)UiCircle(new Vector2(7+x*9,7+y*9),2,color);break;
                    case 103:UiLine(new Vector2(5,4),new Vector2(5,28),color,3);for(int i=0;i<5;i++)Panel(new Rect(10+i*4,14,2,4),color);break;
                }
            }
            finally{GUI.matrix=matrix;}
        }
    }
}
