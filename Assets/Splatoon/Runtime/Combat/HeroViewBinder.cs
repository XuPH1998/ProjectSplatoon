using System;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed class HeroViewBinder : IDisposable
    {
        readonly Transform _root;
        public HeroContent Content { get; private set; }
        public InkCharacterView View { get; private set; }
        public Transform Visual => View != null ? View.transform : null;
        public HeroViewBinder(Transform root) => _root = root;

        public bool Apply(HeroContent content)
        {
            if (Content != null && View != null && Content.CharacterPrefab == content.CharacterPrefab && Content.WeaponPrefab == content.WeaponPrefab)
            { Content = content; return false; }
            var staging = new GameObject("Hero assembly"); staging.SetActive(false); staging.transform.SetParent(_root, false);
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(content.CharacterPrefab, staging.transform, false);
                var view = instance.GetComponent<InkCharacterView>();
                // Detach the authored preview weapon before rebuilding renderer caches.
                if (view.Weapon != null)
                {
                    var old = view.Weapon.gameObject; old.SetActive(false); old.transform.SetParent(staging.transform, false); Destroy(old);
                }
                var weapon = UnityEngine.Object.Instantiate(content.WeaponPrefab, view.WeaponSocket, false);
                weapon.SetActive(true);
                foreach (var child in weapon.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = _root.gameObject.layer;
                view.BindWeapon(weapon.GetComponent<HeroWeaponBindings>(), content.WeaponPrefab);
                var previous = View;
                View = view; Content = content;
                instance.transform.SetParent(_root, false); instance.SetActive(true);
                if (previous != null) { previous.gameObject.SetActive(false); Destroy(previous.gameObject); }
                return true;
            }
            catch { if (instance != null) Destroy(instance); throw; }
            finally { Destroy(staging); }
        }
        public void Dispose()
        {
            if (View != null) { View.gameObject.SetActive(false); Destroy(View.gameObject); }
            View = null; Content = null;
        }
        public static void Destroy(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
