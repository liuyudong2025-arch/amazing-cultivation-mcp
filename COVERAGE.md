# ACS MCP gameplay coverage

This checklist defines “supported” as a structured action with state read-back or a verifying Helper. Merely opening a window is not counted as complete support.

## Implemented through 1.0

- Observe world, map, grids, things, resources, sect, alerts, selection, commands and input mode.
- NPC work priorities, work enable/disable, preferred work, interruption and daily schedule.
- Complete NPC profile, needs, cultivation, skills, health, injuries, equipment and inventory.
- Cultivation mode/behaviour, cultivation-art change with confirmation, and guarded breakthrough start.
- Combat state, target acquisition, start/stop, manual skill cast, automatic skills and artifact cast.
- Native build catalog, material/direction-aware build mode and single-grid verified placement.
- Building state, construction percentage, feng shui, workers and production queue.
- Add/verify and explicitly remove production tasks; this is the common game path used by ordinary crafting buildings.
- Formation node-definition discovery, custom shape authoring with native legality validation, saved member schemes, active formation state, guarded creation and cancellation.
- Alchemy/artifact formula discovery per cultivator plus native building recipe discovery and production queues.
- Master-disciple assignment and native queued secret/manual teaching.
- Native farming/storage/guard/breeding area discovery, creation, grid editing, crop and storage policy, guard membership, breeding occupants/plants and guarded removal.
- Native qi/five-element field sampling, building feng-shui values, room/aura-area inspection, and read-only spirit-gathering/ominous-room planners.
- Native room feng-shui rank and contributing room metrics, breakthrough probability factors, body-cultivation state and quenching control, esoterica catalog, item use, building material change/dismantle jobs, and visitor/animal population readout are implemented in the workspace pending the consolidated live pass.
- Native NPC equipment/unequipment, bed and cultivation-place assignments, animal hunting/slaughter designations, and NPC talk commands are implemented in the workspace pending the consolidated live pass.
- Native artifact-refinement command arguments, source-item selection, artifact/embryo property readout, and quality inspection are implemented in the workspace pending the consolidated live pass.
- Verified heterogeneous layout construction (up to 128 cells), rectangle construction, verified demolition, breakthrough readiness/orchestration and artifact-refinement command/result tracking are implemented in the workspace pending the consolidated live pass.
- Jianghu interaction availability, favour/fate/vigilance, guarded native talk-action dispatch, god-cultivation/divine-city/tribulation/ascension state and the original divine-city management window are implemented in the workspace pending the consolidated live pass.
- School relations, friend points, tendencies and trade-goods state; native offer selection, structured offer read-back and guarded final settlement.
- World-place discovery, unlock/arrival/forecast/cost state, native exploration commands, guarded recall, and structured native story-choice handling.
- Native object/grid commands, map click/drag, FairyGUI semantic input, camera/focus, pause/speed and monitored execution.
- Native cultivation skill-tree discovery and learning through the same validation and `LearnTreeNode` path used by the original skill-tree window, with spell/magic read-back.
- Complete native outspread-region management: region/catalog state, open-region jobs, policies, building slots and construction cancellation, chiefs, members, supervision, disasters, storage, and product collection.
- Official Panda/WuDang DLC installation, Lua-flag, display-name, and installed-content-path discovery; DLC content that enters ordinary jobs, stories, items, areas, or UI remains operable through those structured adapters.
- Native artifact-refining preflight reproduces the `ToilRefining.MakeFaBao` success-rate inputs and reports random quality/qi-inheritance bounds plus twelve-rate chance, followed by actual output property deltas.

## Required for broader full-game coverage

- Breeding/taming optimization policy (native MoveNpc relocation into breeding areas, automatic membership, pregnancy and tame progress are implemented pending live validation).
- One-off DLC/event windows without a stable native manager API remain covered by the semantic FairyGUI adapter and still require scenario-specific live validation.

Each remaining row should be implemented against game-native APIs, covered by automated tests, and then exercised in a disposable live-save scenario before being marked complete.
