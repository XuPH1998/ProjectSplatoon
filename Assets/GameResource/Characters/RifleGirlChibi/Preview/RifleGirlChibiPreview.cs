using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ChibiArt
{
    /// <summary>Optional art-review scene player. It does not drive gameplay.</summary>
    public sealed class RifleGirlChibiPreview : MonoBehaviour
    {
        public Animator Target;
        public AnimationClip[] Clips;
        public Transform SupportGrip;
        public Quaternion SupportHandRotationOffset=Quaternion.identity;
        public Camera ReviewCamera;
        public bool CorrectSupportHand=true;
        PlayableGraph _graph;
        AnimationClipPlayable _clip;
        int _selected;
        double _time;
        bool _paused;
        float _speed=1;
        Transform _upper,_lower,_hand;
        public double PreviewTime=>_time;

        void OnEnable()
        {
            if(Target==null||Clips==null||Clips.Length==0)return;
            Target.enabled=true;Target.fireEvents=false;
            _upper=Target.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lower=Target.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _hand=Target.GetBoneTransform(HumanBodyBones.LeftHand);
            Select(0);
        }
        public void Select(int index)
        {
            if(_graph.IsValid())_graph.Destroy();
            _selected=index;_time=0;Target.Rebind();Target.fireEvents=false;
            _graph=PlayableGraph.Create("RifleGirl Chibi Preview");_graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _clip=AnimationClipPlayable.Create(_graph,Clips[index]);_clip.SetApplyFootIK(false);
            AnimationPlayableOutput.Create(_graph,"RifleGirl Humanoid",Target).SetSourcePlayable(_clip);
            _graph.Play();Evaluate(0);
        }
        void LateUpdate(){if(_graph.IsValid()){if(!_paused)_time+=Time.deltaTime*_speed;Evaluate(_time);}}
        void Evaluate(double time)
        {
            var clip=Clips[_selected];bool death=clip.name.StartsWith("Die");
            _clip.SetTime(death?System.Math.Min(time,clip.length):time%clip.length);_graph.Evaluate(0);
            if(CorrectSupportHand&&!death&&SupportGrip!=null)SolveSupportArm(_upper,_lower,_hand,SupportGrip.position,SupportGrip.rotation*SupportHandRotationOffset);
        }
        public static void SolveSupportArm(Transform upper,Transform lower,Transform hand,Vector3 goal,Quaternion rotation)
        {
            if(upper==null||lower==null||hand==null)return;
            Vector3 start=upper.position,delta=goal-start;
            float a=Vector3.Distance(start,lower.position),b=Vector3.Distance(lower.position,hand.position);
            if(a<.001f||b<.001f||delta.sqrMagnitude<.000001f)return;
            Vector3 direction=delta.normalized;
            float distance=Mathf.Clamp(delta.magnitude,Mathf.Abs(a-b)+.001f,(a+b)*.999f);
            Vector3 bend=Vector3.ProjectOnPlane(lower.position-start,direction).normalized;
            if(bend.sqrMagnitude<.01f)bend=Vector3.Cross(direction,upper.up).normalized;
            float along=(a*a+distance*distance-b*b)/(2*distance);
            Vector3 elbow=start+direction*along+bend*Mathf.Sqrt(Mathf.Max(0,a*a-along*along));
            upper.rotation=Quaternion.FromToRotation(lower.position-start,elbow-start)*upper.rotation;
            lower.rotation=Quaternion.FromToRotation(hand.position-lower.position,goal-lower.position)*lower.rotation;
            hand.rotation=rotation;
        }
        void OnGUI()
        {
            if(Target==null||Clips==null||Clips.Length==0)return;
            GUILayout.BeginArea(new Rect(14,14,230,Mathf.Min(Screen.height-28,540)),GUI.skin.box);
            GUILayout.Label("RifleGirl / 2.5 heads");GUILayout.Label("Original RifleGirl animations");
            for(int i=0;i<Clips.Length;i++)if(GUILayout.Button((_selected==i?"> ":"")+Clips[i].name))Select(i);
            if(GUILayout.Button(_paused?"Resume":"Pause"))_paused=!_paused;
            if(GUILayout.Button("Restart"))Select(_selected);
            GUILayout.Label("Playback: "+_speed.ToString("F2")+"x");_speed=GUILayout.HorizontalSlider(_speed,.1f,2f);
            CorrectSupportHand=GUILayout.Toggle(CorrectSupportHand,"Support hand grip");
            if(ReviewCamera!=null)
            {
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("Front"))View(new Vector3(0,.67f,4));
                if(GUILayout.Button("3/4"))View(new Vector3(2.5f,1.1f,4));
                if(GUILayout.Button("Back"))View(new Vector3(0,.67f,-4));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }
        void View(Vector3 offset){ReviewCamera.transform.position=Target.transform.position+offset;ReviewCamera.transform.LookAt(Target.transform.position+Vector3.up*.64f);}
        void OnDisable(){if(_graph.IsValid())_graph.Destroy();}
    }
}
