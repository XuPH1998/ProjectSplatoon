using UnityEngine;

namespace Splatoon.Skills
{
    public enum SkillEventType { PlayAnimation, OpenHitbox, CloseHitbox, SpawnVfx, PlaySfx, ApplyResource }
    public interface ISkillEventHandler { void Handle(SkillEventData data); }
    public readonly struct SkillEventData { public readonly SkillEventType Type; public readonly float Time; public SkillEventData(SkillEventType type, float time) { Type = type; Time = time; } }
}
