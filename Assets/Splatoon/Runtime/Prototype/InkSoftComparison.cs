using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using Splatoon.Painting;
using UnityEngine.InputSystem;
#endif

namespace Splatoon.Prototype
{
    // Opt-in comparison controls; never starts in a normal launch.
    public sealed class InkSoftComparison : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        InkLook _look=InkLook.Soft;
        PrototypeMatch _match;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-inkSoftCompare")<0)return;
            var go=new GameObject("Ink comparison controls");DontDestroyOnLoad(go);go.AddComponent<InkSoftComparison>();
        }
        void Update()
        {
            var match=PrototypeMatch.Current;
            if(match==null || !match.IsSpawned || !match.InitialSyncComplete)return;
            bool change=Keyboard.current!=null && Keyboard.current.f8Key.wasPressedThisFrame;
            if(change)_look=_look==InkLook.Soft?InkLook.Current:InkLook.Soft;
            if(change || _match!=match)
            {
                _match=match;
                foreach(var s in match.Arena.Surfaces.Values)s.SetInkLook(_look);
                Debug.Log("[INK-COMPARE] "+_look);
            }
        }
        void OnGUI()
        {
            if(_match==null)return;
            GUI.Box(new Rect(12,Screen.height-42,260,30),"F8: "+(_look==InkLook.Soft?"B / Soft ink":"A / Current ink"));
        }
#endif
    }
}
