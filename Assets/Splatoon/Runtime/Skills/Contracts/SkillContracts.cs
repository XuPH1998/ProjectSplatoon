using UnityEngine;

namespace Splatoon.Skills
{
    public enum SkillEventType { [InspectorName("播放动画")] PlayAnimation, [InspectorName("开启伤害判定")] OpenHitbox, [InspectorName("关闭伤害判定")] CloseHitbox, [InspectorName("生成特效")] SpawnVfx, [InspectorName("播放音效")] PlaySfx, [InspectorName("应用资源变化")] ApplyResource }
    public interface ISkillEventHandler { void Handle(SkillEventData data); }
    public readonly struct SkillEventData { public readonly SkillEventType Type; public readonly float Time; public SkillEventData(SkillEventType type, float time) { Type = type; Time = time; } }
}
