# Network State Sync Rules

- NGO Host is authoritative for gameplay state, damage, hit results, random values and match phase.
- Clients submit input frames and display interpolated snapshots.
- Replicate stable IDs and tick numbers; never replicate Unity InstanceID or ScriptableObject references.
- VFX, audio and camera feedback are presentation-only and cannot mutate authoritative state.
- Dedicated server, rollback prediction and matchmaking are future extensions behind `ILanSessionService`.
