# 🎮 DizzleBot – Monitor Control (Streamer.bot Integration)

> Instantly switch your monitor input directly from Streamer.bot  
> Stop touching monitor buttons. Start automating like a pro.

![GitHub stars](https://img.shields.io/github/stars/CanadianZombies/DizzleBot---Monitor-Control?style=for-the-badge)
![GitHub forks](https://img.shields.io/github/forks/CanadianZombies/DizzleBot---Monitor-Control?style=for-the-badge)
![GitHub issues](https://img.shields.io/github/issues/CanadianZombies/DizzleBot---Monitor-Control?style=for-the-badge)

---

## 🚀 What It Does

**DizzleBot – Monitor Control** is a C# automation utility built specifically for integration with **Streamer.bot**.

It communicates with your monitor using **DDC/CI (VCP Code 0x60 – Input Source Selection)** to programmatically switch input sources.

This means:

🎮 Switch from PC → Console instantly  
🖥 Swap HDMI ↔ DisplayPort automatically  
⚡ Trigger input changes from hotkeys, scene changes, or chat commands  
✨ Eliminates physical button wading through OSD menus
✨ Fits naturally into your live automation workflows  

No OSD menus. No physical buttons. No friction.

This is *not* just a script — it’s automation to make your streaming setup feel *pro-level* and effortless.

---

## 🎯 Designed For Streamers

If you:

- Run dual PCs
- Switch between gaming PC and console
- Use capture cards
- Want scene-based hardware automation

This tool is built for you.

---

## 🎯 Why This?

If you stream with multiple machines (PC ↔ console) or frequently switch display sources, this script saves:

- ⏱️ Time
- 🤦‍♂️ Annoying button presses
- 👀 Attention from your audience while you fiddle around

Perfect for streamers who want **automation, not frustration**.

---

# 📊 Project Activity & Live Development

<table>
<tr>
<td align="center" width="50%">

### 🧠 GitHub Activity

![GitHub Streak](https://github-readme-streak-stats.vercel.app/?user=CanadianZombies&theme=dark&hide_border=true)

![Top Languages](https://github-readme-stats.vercel.app/api/top-langs/?username=CanadianZombies&layout=compact&theme=dark&hide_border=true)

</td>

<td align="center" width="50%">

### 🎥 Built Live on Twitch

![Twitch Status](https://img.shields.io/twitch/status/SimmyDizzle?style=for-the-badge&logo=twitch&color=9146FF)
![Twitch Followers](https://img.shields.io/twitch/followers/SimmyDizzle?style=for-the-badge&logo=twitch&color=9146FF)

<br><br>

🔴 Most automation systems are built live.  
🛠 C# + Streamer.bot integrations  
⚡ Hardware control & workflow engineering  

👉 **https://twitch.tv/SimmyDizzle**

---


## 🛠️ Features

- 🖥️ Programmatically switch your monitor’s *input source*
- 📡 Designed specifically for integration with **Streamer.bot**
- 🎙️ Works great with hotkeys, macros, or trigger events such as Twitch Channel Point Redemptions in your automation stack
- 💡 Extendable codebase — easy to experiment or integrate into other systems

---

## 📦 Installation

1. Clone the repo  
   ```bash
   git clone https://github.com/CanadianZombies/DizzleBot---Monitor-Control.git

# ⚙ Configuration

The script contains a configuration section that allows you to control:

- Which monitor is targeted
- Which input ports are defined
- How monitors are selected

---

## 🖥 Monitor Targeting Modes

```csharp
// Set to one of:
// - "PRIMARY" : Use primary/default monitor (0,0 coordinates)
// - "FIRST" : Use first monitor found
// - "MONITOR_NAME:substring" : Match monitor by name
private const string TARGET_MONITOR_MODE = "PRIMARY";
private const string PORT_1 = "DISPLAYPORT"; // -- My PC Port
private const string PORT_2 = "HDMI2";       // -- My Console Port
