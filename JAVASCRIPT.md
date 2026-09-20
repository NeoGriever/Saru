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

`Start` runs once when a script starts. `Stop` may return `true` for a clean shutdown. Available event names are `dialog`, `arrived`, `time`, `reach`, `message`, `zoneChanged`, `zoneChangeStart`, `dutyEnd`, and `eventDone`. Use the constants in `FFEV` to avoid spelling mistakes.

## State and positions

```js
const point = { x: 12.5, y: 3.0, z: -42.75 };
const distance = dist(curPos, point);
const npcDistance = dist(12345);

console.log(curPos.x);
console.log(FFXIV.currentMap);
console.log(inZoneChange);
```

`curPos` is the read-only player position. `FFXIV` exposes `currentMap`, `territoryId`, `instance`, `inZoneChange`, `waitForDuty`, `boundByDuty`, `occupiedInQuestEvent`, `inDutyQueue`, `inCombat`, `mounted`, `jumping`, `isLoggedIn`, and `isPvP`.

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
vNavMesh.MoveTo({ x: 12.5, y: 3.0, z: -42.75 }, 1.5);
vNavMesh.IsRunning();
```

The final `MoveTo` argument is the arrival buffer. `arrived` fires when the player reaches that buffer. `target.Activate` disables Cammy camera no-clipping through Cammy’s loaded public API before the interaction.

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
