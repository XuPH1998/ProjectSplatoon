using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        GameObject _subWeaponPreviewRoot, _subWeaponMarker;
        LineRenderer _subWeaponLine;
        Material _subWeaponPreviewMaterial;
        public bool SubWeaponPreviewVisible { get; private set; }
        public bool SubWeaponPreviewValid { get; private set; }

        void UpdateSubWeaponPreview(PlayerSnapshot state)
        {
            var config = SubWeaponConfigService.Current.Get(state.HeroId);
            bool visible = PrototypeApp.Current != null && PrototypeApp.Current.CanFireInput && state.Health > 0 && !state.Swimming &&
                config != null && config.DeploymentMode == SubWeaponDeploymentMode.HoldPreviewRelease && Mouse.current != null && Mouse.current.rightButton.isPressed;
            SubWeaponPreviewVisible = visible;
            if (!visible) { if (_subWeaponPreviewRoot != null) _subWeaponPreviewRoot.SetActive(false); return; }
            EnsureSubWeaponPreview(); _subWeaponPreviewRoot.SetActive(true);

            Quaternion view = Quaternion.Euler(state.Pitch, state.Yaw, 0);
            Vector3 pivot = state.Position + CameraPivotOffset(state, Presentation);
            Vector3 origin = CameraPosition(pivot, view, Presentation);
            Vector3 direction = view * Vector3.forward;
            bool hitFound = Physics.Raycast(origin, direction, out var hit, config.PlacementRange, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore);
            SubWeaponPreviewValid = hitFound && hit.collider.GetComponentInParent<PaintSurface>() != null && SupportsSurface(config.AttachSurfaces, hit.normal);
            Vector3 end = hitFound ? hit.point : origin + direction * config.PlacementRange;
            Color color = SubWeaponPreviewValid ? PrototypeArena.TeamColor(state.Team) : new Color(1, .22f, .16f);
            _subWeaponLine.positionCount = 2; _subWeaponLine.SetPosition(0, origin); _subWeaponLine.SetPosition(1, end);
            _subWeaponLine.startColor = _subWeaponLine.endColor = color;
            _subWeaponMarker.SetActive(hitFound); _subWeaponMarker.transform.position = end + (hitFound ? hit.normal * .035f : Vector3.zero);
            if (hitFound) _subWeaponMarker.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            _subWeaponMarker.transform.localScale = config.Kind == SubWeaponKind.InkCurtain ? new Vector3(config.Width, .04f, .3f) :
                config.Kind is SubWeaponKind.SpeedPad or SubWeaponKind.JumpPad ? new Vector3(config.Width, .04f, 1.2f) : Vector3.one * Mathf.Max(.25f, config.CollisionRadius * 2);
            var renderer = _subWeaponMarker.GetComponent<Renderer>();
            var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", color); block.SetColor("_Color", color); renderer.SetPropertyBlock(block);
        }

        void EnsureSubWeaponPreview()
        {
            if (_subWeaponPreviewRoot != null) return;
            _subWeaponPreviewRoot = new GameObject("Local sub weapon preview");
            _subWeaponLine = _subWeaponPreviewRoot.AddComponent<LineRenderer>();
            _subWeaponLine.useWorldSpace = true; _subWeaponLine.widthMultiplier = .025f; _subWeaponLine.numCapVertices = 4;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            _subWeaponPreviewMaterial = new Material(shader) { name = "Local sub weapon preview" };
            _subWeaponLine.sharedMaterial = _subWeaponPreviewMaterial;
            _subWeaponMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder); _subWeaponMarker.name = "Placement marker";
            _subWeaponMarker.transform.SetParent(_subWeaponPreviewRoot.transform, false); _subWeaponMarker.layer = 8;
            var collider = _subWeaponMarker.GetComponent<Collider>(); if (collider != null) collider.enabled = false;
            _subWeaponMarker.GetComponent<Renderer>().sharedMaterial = _subWeaponPreviewMaterial;
        }

        void ClearSubWeaponPreview()
        {
            if (_subWeaponPreviewRoot != null) Destroy(_subWeaponPreviewRoot);
            if (_subWeaponPreviewMaterial != null) Destroy(_subWeaponPreviewMaterial);
            _subWeaponPreviewRoot = _subWeaponMarker = null; _subWeaponLine = null; _subWeaponPreviewMaterial = null;
            SubWeaponPreviewVisible = SubWeaponPreviewValid = false;
        }

        static bool SupportsSurface(SubWeaponSurfaceMask mask, Vector3 normal)
        {
            SubWeaponSurfaceMask surface = normal.y >= .65f ? SubWeaponSurfaceMask.Floor : normal.y <= -.65f ? SubWeaponSurfaceMask.Ceiling : SubWeaponSurfaceMask.Wall;
            return (mask & surface) != 0;
        }
    }
}
