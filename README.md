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

---

## 🧠 Control Logic Overview

| System       | Strategy                                                                 |
|--------------|--------------------------------------------------------------------------|
| Core Temp    | PID reactivity based on slope from desired range (310–360 °C)            |
| Rods         | One rod bank adjusted per tick using PID logic (with scaling)            |
| Xenon/Iodine | Iodine tracked using moving average, boron dosed to suppress generation  |
| Boron        | PID loop to hold ~3300 PPM, with rule-based overrides for edge cases     |
| Condenser    | PID adjusts pump speed to hold condenser return temp ~65 °C              |
| Emergencies  | If xenon > 60 + temp < 300 + reactivity < 0 → withdraw all rods slowly   |


## 📦 Variables Used

- `CORE_TEMP`
- `CORE_XENON_CUMULATIVE`
- `CORE_IODINE_CUMULATIVE`
- `CORE_STATE_CRITICALITY`
- `RODS_QUANTITY`
- `ROD_BANK_POS_0_ORDERED` through `ROD_BANK_POS_8_ORDERED`
- `ROD_BANK_POS_*_ACTUAL`
- `CHEM_BORON_PPM`
- `CHEM_BORON_DOSAGE_ORDERED_RATE`
- `CHEM_BORON_FILTER_ORDERED_SPEED`
- `CONDENSER_TEMPERATURE`
- `CONDENSER_CIRCULATION_PUMP_ORDERED_SPEED`

## 👨‍🔬 Example Output

Temp: 322.5 °C | Reactivity: 0.08 | Rods: 56.2% | Bank: 4
Xenon: 42.16 | Iodine: 121.57 | In temp range: True
Condenser Temp: 66.7 °C | Condenser Speed: 5.34%
[BORON] PPM: 3341.2 | PID Out: -1.23 | Dose: 0.0 | Filter: 1.2
[IODINE AVG] 1.05 | Reactivity: 0.08 | Temp: 322.5
