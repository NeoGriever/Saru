# Saru JavaScript Reference

Saru discovers `.js` files in its Scripts folder automatically. Each script can run independently. Use the per-script **Play** and **Stop** buttons, `/saru start <scriptname>`, or `/saru stop`.

## Lifecycle

```js
function Start() {}
function Stop() { return true; }

addEventListener("dialog", () => true);
addEventListener("arrived", () => {});
addEventListener("time", () => {});
addEventListener("reach", () => {});
addEventListener("message", message => {});
```

`Start` runs once when a script starts. `Stop` may return `true` for a clean shutdown. Available event names are `dialog`, `arrived`, `time`, `reach`, `message`, `mapChange`, `zoneChanged`, `zoneChangeStart`, `dutyEnd`, and `eventDone`. `mapChange` fires whenever `FFXIV.map.id` changes; use the constants in `FFEV` to avoid spelling mistakes.

## Script configuration

Declare settings directly at script root. Saru evaluates these declarations when a script is discovered or reloaded, shows them in the expandable **Configuration** section for that script, and injects the selected values when the script starts.

```js
const enableFeature = saru.SetConfig("enableFeature", "Enable feature", saru.configType.checkbox, [], false);
const destination = saru.SetConfig("destination", "Destination", saru.configType.combo, ["None", "Option 2", "Option 3"], 0);
const message = saru.SetConfig("message", "Message", saru.configType.input, [], "Example");
const delay = saru.SetConfig("delay", "Delay", saru.configType.number, [0, 10], 4);

function Start() {
  if (enableFeature && destination === 1) console.log(`${message}: ${delay}`);
}
```

`checkbox` and `input` may omit their unused empty options array, for example `saru.SetConfig("enabled", "Enabled", saru.configType.checkbox, false)`. `SetConfig` returns the configured value: `boolean` for `checkbox`, a zero-based `number` index for `combo`, `string` for `input`, and a numeric value for `number`. Number fields are edited as whole numbers with `-`, direct input, and `+` controls. The declarations must be at root level. Function bodies are not called during configuration discovery; only the isolated root evaluation runs, with no game or automation APIs available. Compatible values are retained when the script is reloaded; changed or new entries use their declared default.

## State and positions

```js
const point = { x: 12.5, y: 3.0, z: -42.75, mapId: 144 };
const distance = dist(curPos, point);
const npcDistance = dist(12345);

console.log(curPos.x);
console.log(curPos.mapId);
console.log(FFXIV.currentMap);
console.log(FFXIV.map.id);
console.log(inZoneChange);
```

`curPos` is the read-only player position and includes `mapId`. `FFXIV.currentMap` and `FFXIV.map.id` expose the current map ID; use the latter when treating the map as an object. `FFXIV` also exposes `territoryId`, `instance`, `inZoneChange`, `waitForDuty`, `boundByDuty`, `occupiedInQuestEvent`, `inDutyQueue`, `inCombat`, `mounted`, `jumping`, `isLoggedIn`, and `isPvP`.

## Timers, targets, and movement

```js
Timer.At(time.getTime() + 60);
Timer.Un(time.getTime() + 60);
Timer.UnI(0);

const once = setTimeout(() => {}, 1000);
const repeated = setInterval(() => {}, 5000);
clearTimeout(once);
clearInterval(repeated);

target.At(12345, 4);
target.Un(12345);
target.Select(12345);
target.Activate(12345);
target.Activate();

vNavMesh.MoveTo(12.5, 3.0, -42.75, 1.5);
vNavMesh.MoveTo({ x: 12.5, y: 3.0, z: -42.75, mapId: 144 }, 1.5);
vNavMesh.IsRunning();
```

The final `MoveTo` argument is the arrival buffer. `mapId` is optional: without it movement behaves as before; with it, the move is rejected unless the player is on that map, and `arrived` cannot fire on another map. `target.Activate` disables Cammy camera no-clipping through Cammy’s loaded public API before the interaction.

## Chocoholic

When the optional Chocoholic plugin is loaded, its racing workflow can be controlled through Saru:

```js
chocoholic.Toggle(true);  // activate Chocoholic racing
chocoholic.Toggle(false); // deactivate Chocoholic racing
chocoholic.SetNumberOfRaces(1); // set Number of races (clamped to 0–999)
```

`Toggle` returns `false` if Chocoholic is not loaded or its compatible activation state is unavailable. Chocoholic currently exposes no general start/stop IPC, so Saru sets the same internal `DutyRestart.Enabled` state that its **Start Racing**/**Stop Racing** button uses through reflection.

## Dialogs and chat

```js
WaitForDialog.Select(true);
WaitForDialog.Select(false);
WaitForDialog.Y();
WaitForDialog.N();

sendMsg("/say Hello");
console.info("Logged information");
console.log("Visible echo message");
console.error("Error message");
Exit();
```

## Saucy integration

`Saucy` is available when the Saucy plugin is loaded. Its primary APIs are `Saucy.npcs`, `Saucy.cards[name]`, `Saucy.cuffacur`, `Saucy.outonalimb`, and `Saucy.sliceisright.automove`.

```js
Saucy.cuffacur.Toggle(true);
Saucy.cuffacur.fmc.Toggle(true);
Saucy.cuffacur.fmc.Set(10);

for (const npc of Saucy.npcs) {
  for (const card of npc.cards) {
    if (!card.obtained()) console.info(card.Name);
  }
}
```
