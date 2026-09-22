using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Combat
{
    public sealed class RescueBubbleView : MonoBehaviour
    {
        GameObject _sphere;
        Material _material;
        Renderer _renderer;
        bool _initialized;
        uint _event;
        double _burstAt = double.NegativeInfinity;
        Vector3 _burstPosition;
        float _radius;
        BubbleOutcome _outcome;
        byte _team;
        void Ensure()
        {
            if (_sphere != null) return;
            _sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere); _sphere.name = "待救泡泡"; _sphere.layer = 2;
            var collider = _sphere.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
            _sphere.transform.SetParent(transform, false);
            _renderer = _sphere.GetComponent<Renderer>(); _renderer.shadowCastingMode = ShadowCastingMode.Off; _renderer.receiveShadows = false;
            _material = new Material(Resources.Load<Shader>("RescueBubble")); _renderer.sharedMaterial = _material;
        }
        public void Present(PlayerSnapshot state, Vector3 displayedFeet, bool selected, double now)
        {
            Ensure();
            if (!state.IsBubble && state.BubbleResult == BubbleOutcome.None) _burstAt = double.NegativeInfinity;
            if (_initialized && state.BubbleEvent != _event && state.BubbleResult != BubbleOutcome.None && now - state.BubbleEndedAt < .65)
            {
                _burstAt = state.BubbleEndedAt; _burstPosition = state.BubbleEndPosition; _radius = state.BubbleRadius;
                _outcome = state.BubbleResult; _team = state.BubbleInkTeam;
            }
            _initialized = true; _event = state.BubbleEvent;
            float burst = (float)(now - _burstAt) / .65f;
            bool bursting = !state.IsBubble && burst >= 0 && burst < 1;
            _sphere.SetActive(state.IsBubble || bursting);
            if (!_sphere.activeSelf) return;
            Color color = PrototypeArena.TeamColor(state.Team);
            if (state.IsBubble)
            {
                _sphere.transform.position = displayedFeet + Vector3.up * state.BubbleCenterHeight;
                _sphere.transform.localScale = Vector3.one * (2 * state.BubbleRadius);
                _material.SetFloat("_Fade", 1); _material.SetFloat("_Selected", selected ? 1 : 0); _material.SetFloat("_Burst", 0);
            }
            else
            {
                color = _outcome == BubbleOutcome.Rescued ? new Color(.5f, 1, .85f) : PrototypeArena.TeamColor(_team);
                _sphere.transform.position = _burstPosition;
                _sphere.transform.localScale = Vector3.one * (2 * _radius * (1 + burst * (_outcome == BubbleOutcome.Rescued ? .3f : .65f)));
                _material.SetFloat("_Fade", 1 - burst); _material.SetFloat("_Selected", 1); _material.SetFloat("_Burst", burst);
            }
            _material.SetColor("_BubbleColor", color);
        }
        void OnDestroy() { if (_sphere != null) Destroy(_sphere); if (_material != null) Destroy(_material); }
    }
}
