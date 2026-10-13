# Hollow Knight progression logic

`transitions.json`, `waypoints.json`, `macros.json` and `locations.json` are Hollow Knight Randomizer 4's logic
(homothetyhk/RandomizerMod, commit cad3d5a7b73ed3ddf4af43795b20c23ced14bb4c, `RandomizerMod/Resources/Logic`),
unchanged, under the GNU LGPL 2.1 (`RandomizerMod-LICENSE.txt`). The mod reads them (`GateLogic`) to keep Hollow
Knight's ability gates: a door the Knight couldn't reach with the abilities you have is closed by an invisible wall, and an item it couldn't reach can't be
picked up yet.
