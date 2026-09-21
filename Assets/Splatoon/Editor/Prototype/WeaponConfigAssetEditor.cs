#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(WeaponConfigAsset))]
    [CanEditMultipleObjects]
    public sealed class WeaponConfigAssetEditor : UnityEditor.Editor
    {
        static readonly string[] Selectors =
        {
            nameof(WeaponConfigAsset.weaponPrefabAddress), nameof(WeaponConfigAsset.ammoConfig),
            nameof(WeaponConfigAsset.fireMode), nameof(WeaponConfigAsset.motionMode), nameof(WeaponConfigAsset.muzzleMode),
            nameof(WeaponConfigAsset.referenceRules), nameof(WeaponConfigAsset.referenceSpreadEnabled), nameof(WeaponConfigAsset.shooterDetails)
        };

        static readonly string[] SectionOrder =
        {
            "发射", "弹道", "伤害", "涂色", "散布", "连发", "半自动", "蓄力", "旋转枪",
            "泡泡连发", "泡泡弹道", "漂浮泡泡", "爆破枪", "普通双枪（原作参数的项目近似）",
            "参考弹道", "参考散布", "参考涂墨", "参考泡泡", "射手详细规则（独立启用）", "爆炸泼桶"
        };

        static readonly FieldInfo[] Fields = typeof(WeaponConfigAsset)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .OrderBy(field => field.MetadataToken).ToArray();
        static readonly IGrouping<string, FieldInfo>[] Sections = BuildSections();

        // Use pending serialized values so a selector change takes effect in the same draw.
        // Mixed selectors retain each asset's own value until explicitly edited.
        readonly struct Selection
        {
            public readonly WeaponFireMode Fire;
            public readonly ProjectileMotionMode Motion;
            public readonly bool Reference, ReferenceSpread, Shooter, Detailed, Inherit;

            public Selection(SerializedObject serialized, WeaponConfigAsset asset)
            {
                var fire = serialized.FindProperty(nameof(asset.fireMode));
                var motion = serialized.FindProperty(nameof(asset.motionMode));
                var reference = serialized.FindProperty(nameof(asset.referenceRules));
                var spread = serialized.FindProperty(nameof(asset.referenceSpreadEnabled));
                var shooter = serialized.FindProperty(nameof(asset.shooterDetails));
                var detailed = serialized.FindProperty(nameof(asset.detailedPaint));
                var inherit = serialized.FindProperty(nameof(asset.inheritForwardMovement));
                Fire = fire.hasMultipleDifferentValues ? asset.fireMode : (WeaponFireMode)fire.intValue;
                Motion = motion.hasMultipleDifferentValues ? asset.motionMode : (ProjectileMotionMode)motion.intValue;
                Reference = reference.hasMultipleDifferentValues ? asset.referenceRules : reference.boolValue;
                ReferenceSpread = Reference && (spread.hasMultipleDifferentValues ? asset.referenceSpreadEnabled : spread.boolValue);
                Shooter = shooter.hasMultipleDifferentValues ? asset.shooterDetails : shooter.boolValue;
                Detailed = detailed.hasMultipleDifferentValues ? asset.detailedPaint : detailed.boolValue;
                Inherit = inherit.hasMultipleDifferentValues ? asset.inheritForwardMovement : inherit.boolValue;
            }
        }

        Selection[] ReadSelection() => targets.Cast<WeaponConfigAsset>().Select(asset => new Selection(serializedObject, asset)).ToArray();

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("在编辑器的“单机武器调试”房间修改可实时试枪。普通联机房间使用入房时的配置。参数修改按正常资产保存流程持久化。", MessageType.Info);
            serializedObject.Update();
            float labelWidth = EditorGUIUtility.labelWidth;
            try
            {
                EditorGUIUtility.labelWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * .62f, 180, 430);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"), new GUIContent("脚本"));

                DrawHeading("通用配置与模式");
                foreach (string name in Selectors)
                {
                    var selection = ReadSelection();
                    if (selection.All(value => IsVisible(name, value))) DrawField(Fields.First(field => field.Name == name));
                }

                if (serializedObject.isEditingMultipleObjects)
                    EditorGUILayout.HelpBox("多选时仅显示所有选中武器都适用的字段；单独选择资产可调整其余专用参数。", MessageType.Info);

                var current = ReadSelection();
                foreach (var section in Sections)
                {
                    bool headingDrawn = false;
                    foreach (var field in section)
                    {
                        if (!current.All(value => IsVisible(field.Name, value))) continue;
                        if (!headingDrawn) { DrawHeading(section.Key); headingDrawn = true; }
                        DrawField(field);
                    }
                }
                serializedObject.ApplyModifiedProperties();
            }
            finally { EditorGUIUtility.labelWidth = labelWidth; }
            foreach (var asset in targets)
            {
                try { WeaponConfigValidation.Validate(((WeaponConfigAsset)asset).Snapshot()); }
                catch (Exception e) { EditorGUILayout.HelpBox("当前修改不可应用：" + e.Message, MessageType.Error); }
            }
        }

        static void DrawHeading(string title)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        void DrawField(FieldInfo field)
        {
            var property = serializedObject.FindProperty(field.Name);
            var label = new GUIContent(WeaponConfigLabels.Name(field.Name), field.GetCustomAttribute<TooltipAttribute>()?.tooltip);
            var rect = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(rect, label, property);
            bool mixed = EditorGUI.showMixedValue;
            try
            {
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                // Headings are independent of the field originally carrying HeaderAttribute.
                // Keep edits on SerializedProperty to preserve Undo, mixed values and saving.
                if (field.FieldType.IsEnum)
                {
                    var names = Enum.GetNames(field.FieldType);
                    var options = names.Select(name => new GUIContent(field.FieldType.GetField(name).GetCustomAttribute<InspectorNameAttribute>()?.displayName ?? name)).ToArray();
                    var values = Enum.GetValues(field.FieldType).Cast<object>().Select(Convert.ToInt32).ToArray();
                    int value = EditorGUI.IntPopup(rect, label, property.intValue, options, values);
                    if (EditorGUI.EndChangeCheck()) property.intValue = value;
                }
                else if (field.FieldType == typeof(double))
                {
                    double value = EditorGUI.DoubleField(rect, label, property.doubleValue);
                    if (EditorGUI.EndChangeCheck()) property.doubleValue = value;
                }
                else if (field.FieldType == typeof(float))
                {
                    float value = EditorGUI.FloatField(rect, label, property.floatValue);
                    if (EditorGUI.EndChangeCheck()) property.floatValue = value;
                }
                else if (field.FieldType == typeof(int))
                {
                    int value = EditorGUI.IntField(rect, label, property.intValue);
                    if (EditorGUI.EndChangeCheck()) property.intValue = value;
                }
                else if (field.FieldType == typeof(bool))
                {
                    bool value = EditorGUI.Toggle(rect, label, property.boolValue);
                    if (EditorGUI.EndChangeCheck()) property.boolValue = value;
                }
                else if (field.FieldType == typeof(string))
                {
                    string value = EditorGUI.TextField(rect, label, property.stringValue);
                    if (EditorGUI.EndChangeCheck()) property.stringValue = value;
                }
                else if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                {
                    var value = EditorGUI.ObjectField(rect, label, property.objectReferenceValue, field.FieldType, false);
                    if (EditorGUI.EndChangeCheck()) property.objectReferenceValue = value;
                }
                else
                {
                    EditorGUI.EndChangeCheck();
                    EditorGUI.PropertyField(rect, property, label, true);
                }
            }
            finally
            {
                EditorGUI.showMixedValue = mixed;
                EditorGUI.EndProperty();
            }
        }

        static bool IsVisible(string name, Selection value)
        {
            bool charge = value.Fire == WeaponFireMode.Charge;
            bool splatling = value.Fire == WeaponFireMode.Splatling;
            bool bubble = value.Motion == ProjectileMotionMode.BouncingBubble;
            bool dualies = value.Motion == ProjectileMotionMode.DualiesNormal;
            bool blaster = value.Fire == WeaponFireMode.Blaster;
            switch (name)
            {
                case nameof(WeaponConfigAsset.fireRate):
                    return !blaster && value.Fire != WeaponFireMode.BubbleVolley;
                case nameof(WeaponConfigAsset.burstCount):
                    return value.Fire == WeaponFireMode.Burst || value.Fire == WeaponFireMode.BubbleVolley;
                case nameof(WeaponConfigAsset.burstRecoverySeconds):
                    return value.Fire == WeaponFireMode.Burst;
                case nameof(WeaponConfigAsset.semiBufferSeconds):
                    return value.Fire == WeaponFireMode.SemiAutomatic || blaster || value.Fire == WeaponFireMode.Explosher;
                case nameof(WeaponConfigAsset.chargeSeconds):
                case nameof(WeaponConfigAsset.chargePartialMaxDamage):
                case nameof(WeaponConfigAsset.chargeMinRange):
                case nameof(WeaponConfigAsset.chargeMinSpeed):
                    return charge || splatling;
                case nameof(WeaponConfigAsset.landingSpreadRecoverSeconds):
                    return charge && !value.ReferenceSpread && !dualies;
                case nameof(WeaponConfigAsset.bubbleVolleySeconds):
                case nameof(WeaponConfigAsset.bubbleIntervalSeconds):
                    return value.Fire == WeaponFireMode.BubbleVolley;
                case nameof(WeaponConfigAsset.bubbleGroundBounces):
                case nameof(WeaponConfigAsset.bubbleMaxBounces):
                case nameof(WeaponConfigAsset.bubbleNormalRetention):
                case nameof(WeaponConfigAsset.bubbleTangentRetention):
                case nameof(WeaponConfigAsset.bubbleWallRetention):
                    return bubble;
                case nameof(WeaponConfigAsset.floatingPitchSpreadDegrees):
                    return value.Motion == ProjectileMotionMode.FloatingBubble;
                case nameof(WeaponConfigAsset.blasterRepeatSeconds):
                case nameof(WeaponConfigAsset.blasterPostSeconds):
                case nameof(WeaponConfigAsset.blasterPlayerRadius):
                    return blaster;
                case nameof(WeaponConfigAsset.blasterTrailCount):
                    return blaster && !value.Reference;
                case nameof(WeaponConfigAsset.blasterBrakeEndSpeed):
                case nameof(WeaponConfigAsset.blasterBrakeDrag):
                case nameof(WeaponConfigAsset.blasterBrakeGravity):
                    return value.Motion == ProjectileMotionMode.TimedBlaster && !value.Reference;
                case nameof(WeaponConfigAsset.splatlingFootEvery):
                case nameof(WeaponConfigAsset.splatlingFootRadius):
                case nameof(WeaponConfigAsset.splatlingTrailCount):
                case nameof(WeaponConfigAsset.splatlingPlayerRadius):
                    return splatling && !value.Reference;
                case nameof(WeaponConfigAsset.splatlingSpreadBias):
                    return splatling && !value.ReferenceSpread;
                case nameof(WeaponConfigAsset.dualiesBrakeEndSpeed):
                case nameof(WeaponConfigAsset.dualiesBrakeDrag):
                case nameof(WeaponConfigAsset.dualiesBrakeGravity):
                case nameof(WeaponConfigAsset.dualiesPlayerRadius):
                case nameof(WeaponConfigAsset.dualiesTrailStartDistance):
                case nameof(WeaponConfigAsset.dualiesTrailCount):
                case nameof(WeaponConfigAsset.dualiesFootEvery):
                case nameof(WeaponConfigAsset.dualiesFootRadius):
                    return dualies && !value.Reference;
                case nameof(WeaponConfigAsset.referenceRules):
                    return true;
                case nameof(WeaponConfigAsset.referenceSpreadEnabled):
                    return value.Reference;
                case nameof(WeaponConfigAsset.referencePitchBias):
                    return splatling && value.Reference;
                case nameof(WeaponConfigAsset.referenceFootDepth):
                    return value.Reference && value.Fire != WeaponFireMode.Explosher;
                case nameof(WeaponConfigAsset.shooterDetails):
                    // Leave an enabled toggle reachable when correcting an incompatible mode.
                    return value.Shooter || value.Fire == WeaponFireMode.Automatic && value.Motion == ProjectileMotionMode.ReferencePhased;
                case nameof(WeaponConfigAsset.shooterPostSeconds):
                    return value.Fire == WeaponFireMode.Automatic || value.Fire == WeaponFireMode.Splatling || blaster || dualies;
                case nameof(WeaponConfigAsset.shooterMoveForwardRate):
                    return value.Inherit || value.Shooter;
                case nameof(WeaponConfigAsset.spreadExpandSeconds):
                case nameof(WeaponConfigAsset.spreadRecoverSeconds):
                case nameof(WeaponConfigAsset.baseSpreadDegrees):
                case nameof(WeaponConfigAsset.baseJumpSpreadDegrees):
                    return !charge && !blaster && !dualies && !value.ReferenceSpread;
                case nameof(WeaponConfigAsset.brakeSpeedMultiplier):
                    return !value.Reference && !dualies && value.Motion != ProjectileMotionMode.TimedBlaster && !bubble;
                case nameof(WeaponConfigAsset.explosherPostSeconds):
                case nameof(WeaponConfigAsset.explosherMoveLimitSeconds):
                case nameof(WeaponConfigAsset.explosherFootDepth):
                    return value.Fire == WeaponFireMode.Explosher;
            }
            if (name.StartsWith("charge", StringComparison.Ordinal)) return charge;
            if (name.StartsWith("splatling", StringComparison.Ordinal)) return splatling;
            if (name.StartsWith("dualies", StringComparison.Ordinal)) return dualies;
            if (name.StartsWith("bubble", StringComparison.Ordinal)) return bubble && value.Reference;
            if (name.StartsWith("explosher", StringComparison.Ordinal)) return value.Motion == ProjectileMotionMode.Explosher;
            if (name.StartsWith("shooter", StringComparison.Ordinal))
                return value.Detailed || value.Shooter;
            if (name.StartsWith("referenceBias", StringComparison.Ordinal) || name.StartsWith("referenceJump", StringComparison.Ordinal))
                return value.ReferenceSpread;
            if (name.StartsWith("reference", StringComparison.Ordinal) || ReferencePaintField(name)) return value.Reference;
            return true; // Common fields (including future additions) stay accessible.
        }

        static bool ReferencePaintField(string name) => name.StartsWith("paintDepth", StringComparison.Ordinal) ||
            name.StartsWith("paintDistance", StringComparison.Ordinal) || name.StartsWith("paintDrop", StringComparison.Ordinal) ||
            name.StartsWith("wallDrop", StringComparison.Ordinal) || name == nameof(WeaponConfigAsset.paintBreakHeight) ||
            name == nameof(WeaponConfigAsset.trailDepthScale) || name == nameof(WeaponConfigAsset.collisionExplosionPaintRadius);

        static IGrouping<string, FieldInfo>[] BuildSections()
        {
            string header = "通用配置";
            return Fields.Select(field =>
            {
                header = field.GetCustomAttribute<HeaderAttribute>()?.header ?? header;
                return (field, section: Section(field.Name, header));
            }).Where(entry => !Selectors.Contains(entry.field.Name))
                .GroupBy(entry => entry.section, entry => entry.field)
                .OrderBy(group => { int index = Array.IndexOf(SectionOrder, group.Key); return index < 0 ? int.MaxValue : index; }).ToArray();
        }

        static string Section(string name, string header)
        {
            switch (name)
            {
                case nameof(WeaponConfigAsset.burstCount):
                case nameof(WeaponConfigAsset.burstRecoverySeconds): return "连发";
                case nameof(WeaponConfigAsset.semiBufferSeconds): return "半自动";
                case nameof(WeaponConfigAsset.bubbleVolleySeconds):
                case nameof(WeaponConfigAsset.bubbleIntervalSeconds): return "泡泡连发";
                case nameof(WeaponConfigAsset.floatingPitchSpreadDegrees): return "漂浮泡泡";
                case nameof(WeaponConfigAsset.landingSpreadRecoverSeconds): return "蓄力";
                case nameof(WeaponConfigAsset.referencePitchBias): return "旋转枪";
            }
            if (name.StartsWith("referenceBrake", StringComparison.Ordinal) || name == nameof(WeaponConfigAsset.referenceFreeDrag) || name == nameof(WeaponConfigAsset.referencePlayerRadius)) return "参考弹道";
            if (name.StartsWith("referenceBias", StringComparison.Ordinal) || name.StartsWith("referenceJump", StringComparison.Ordinal)) return "参考散布";
            if (name.StartsWith("reference", StringComparison.Ordinal) || ReferencePaintField(name)) return "参考涂墨";
            if (name.StartsWith("bubble", StringComparison.Ordinal) && header == "喷3 11.3.0 参考规则") return "参考泡泡";
            return header;
        }
    }
}
#endif
