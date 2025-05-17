# ☢️ NuclearesController

A real-time C# control program for the game [Nucleares](https://store.steampowered.com/app/2589480/Nucleares), designed to autonomously operate a nuclear reactor by adjusting control rods, condenser pumps, and boron levels to maintain optimal performance and avoid Xenon poisoning.

---

## 🎯 Features

- 🔥 **Temperature Regulation**  
  Maintains core temperature between **310°C and 360°C** using PID-controlled reactivity.

- ⚙️ **Smart Reactivity Management**  
  Dynamically adjusts control rods to keep reactivity around **0.10** while gently correcting for temperature drifts.

- 🧪 **Boron Automation**  
  - Tracks iodine generation over time using a smoothed moving average  
  - Doses or filters boron to stabilize iodine levels  
  - Uses a secondary PID loop to maintain **~3300 PPM boron** when not in override

- 🧬 **Xenon Pit Prevention**  
  Monitors iodine and xenon build-up, applying proactive boron adjustments to prevent poisoning during long runs.

- 🛑 **Emergency Overrides**  
  If Xenon > 60, temperature < 300, and reactivity < 0 — all rods are withdrawn gradually to rescue the core.

- 📊 **Console Debug Output**  
  Real-time logs for:
  - Temperature, reactivity, rod positions
  - Xenon and iodine levels
  - Boron dose/filter rates and PID output

---

## 🛠 Requirements

- .NET 7.0 or later
- A running Nucleares game session with the in-game webserver enabled (default `http://localhost:8785`)
- The `PID.cs` file included in the same namespace (basic PID controller implementation)
