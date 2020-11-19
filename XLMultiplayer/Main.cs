﻿using System;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using ReplayEditor;
using System.Linq;
using XLMultiplayerUI;
using Newtonsoft.Json;
using UnityEngine.UI;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Diagnostics;

namespace XLMultiplayer {
	class Server {
		public string name;
		public string ip;
		public string port;
		public string version;
		public string mapName;
		public int playerMax;
		public int playerCurrent;
	}

	public class PreviousUsername {
		[JsonProperty("Previous_Name")]
		public string username = "Username";
	}

	[Serializable]
	public class MultiplayerSettings : UnityModManager.ModSettings {
		public float volumeMultiplier = 1.0f;
		
		public override void Save(UnityModManager.ModEntry modEntry) {
			Save(this, modEntry);
		}
	}

	static class ModMenuGUIPatch {
		public static bool Prefix() {
			return !Main.enabled;
		}
	}

	class Main {
		public static bool enabled;
		public static String modId;
		public static UnityModManager.ModEntry modEntry;

		public static Harmony harmonyInstance;

		public static MultiplayerUtilityMenu utilityMenu;
		public static MultiplayerController multiplayerController;

		public static List<MultiplayerRemotePlayerController> remoteReplayControllers = new List<MultiplayerRemotePlayerController>();

		public static StreamWriter debugWriter;

		public static AssetBundle uiBundle;

		public static MultiplayerSettings settings;

		public static MethodInfo ModMenuGUIMethod = null;
		public static MethodInfo ModMenuGUIPrefix = null;
		public static bool patched = false;

		public static List<Plugin> pluginList = new List<Plugin>();

		public static float lastConnect = 0f;

		static void Load(UnityModManager.ModEntry modEntry) {
			Main.modEntry = modEntry;
			Main.modId = modEntry.Info.Id;

			modEntry.OnToggle = OnToggle;

			string directory = Directory.GetCurrentDirectory();
			try {
				File.Copy(modEntry.Path + "GameNetworkingSockets.dll", directory + "\\GameNetworkingSockets.dll", true);
				File.Copy(modEntry.Path + "libprotobuf.dll", directory + "\\libprotobuf.dll", true);
				File.Copy(modEntry.Path + "libcrypto-1_1-x64.dll", directory + "\\libcrypto-1_1-x64.dll", true);
				File.Copy(modEntry.Path + "System.Buffers.dll", directory + "\\System.Buffers.dll", true);
				File.Copy(modEntry.Path + "System.Memory.dll", directory + "\\System.Memory.dll", true);
				File.Copy(modEntry.Path + "System.Numerics.Vectors.dll", directory + "\\System.Numerics.Vectors.dll", true);
				File.Copy(modEntry.Path + "System.Runtime.CompilerServices.Unsafe.dll", directory + "\\System.Runtime.CompilerServices.Unsafe.dll", true);
			} catch (Exception) { }

			string pluginDirectory = Path.Combine(modEntry.Path, "Plugins");
			if (Directory.Exists(pluginDirectory)) {
				foreach(string subdir in Directory.GetDirectories(pluginDirectory))
					ClearDirectory(subdir);
			}
			settings = MultiplayerSettings.Load<MultiplayerSettings>(modEntry);

			LoadPlugins();
		}

		static bool OnToggle(UnityModManager.ModEntry modEntry, bool value) {
			if (value == enabled) return true;
			enabled = value;

			if (enabled) {
				var mod = UnityModManager.FindMod("blendermf.XLShredMenu");
				if (mod != null) {
					modEntry.CustomRequirements = $"Mod {mod.Info.DisplayName} incompatible";
					enabled = false;
					return false;
				}

				//Patch the replay editor
				harmonyInstance = new Harmony(modEntry.Info.Id);
				harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

				utilityMenu = new GameObject().AddComponent<MultiplayerUtilityMenu>();
				GameObject.DontDestroyOnLoad(utilityMenu.gameObject);

				if (NewMultiplayerMenu.Instance == null) {
					if (uiBundle == null) uiBundle = AssetBundle.LoadFromFile(modEntry.Path + "multiplayerui");

					GameObject newMenuObject = GameObject.Instantiate(uiBundle.LoadAsset<GameObject>("Assets/Prefabs/Multiplayer Menu Container.prefab"));
					NewMultiplayerMenu.Instance.UpdateCallback = MenuUpdate;
					NewMultiplayerMenu.Instance.OnClickConnectCallback = Main.OnClickConnect;
					NewMultiplayerMenu.Instance.OnClickDisconnectCallback = Main.OnClickDisconnect;
					NewMultiplayerMenu.Instance.SaveVolume = Main.SaveSettings;



					GameObject.DontDestroyOnLoad(newMenuObject);

					NewMultiplayerMenu.Instance.StartCoroutine(Main.StartUpdatingServerList());
				}

				MultiplayerUtils.StartMapLoading();

				NewMultiplayerMenu.Instance.VolumeInput.text = settings.volumeMultiplier.ToString("0.000");
				NewMultiplayerMenu.Instance.VolumeSlider.value = settings.volumeMultiplier;
			} else {
				//Unpatch the replay editor
				harmonyInstance.UnpatchAll(harmonyInstance.Id);
				if(ModMenuGUIMethod != null) {
					harmonyInstance.Unpatch(ModMenuGUIMethod, ModMenuGUIPrefix);
					ModMenuGUIMethod = null;
					ModMenuGUIPrefix = null;
					patched = false;
				}

				MultiplayerUtils.StopMapLoading();

				if (multiplayerController != null) multiplayerController.DisconnectFromServer();

				GameObject.Destroy(NewMultiplayerMenu.Instance.gameObject);
				UnityEngine.Object.Destroy(utilityMenu.gameObject);
			}

			return true;
		}

		public static void LoadPlugins() {
			string pluginDirectory = Path.Combine(modEntry.Path, "Plugins");

			if (!Directory.Exists(pluginDirectory)) {
				Directory.CreateDirectory(pluginDirectory);
				return;
			}

			List<string> pluginDirectories = new List<string>();
			foreach (string dir in Directory.GetDirectories(pluginDirectory)) {
				pluginDirectories.Add(dir);
			}

			List<Tuple<string, string>> directoryToHash = new List<Tuple<string, string>>();

			foreach (string file in Directory.GetFiles(pluginDirectory)) {
				if (Path.GetExtension(file).Equals(".zip", StringComparison.CurrentCultureIgnoreCase)) {
					string fileHash = MultiplayerUtils.CalculateMD5Bytes(File.ReadAllBytes(file));
					bool pluginLoaded = false;
					foreach (Plugin plugin in pluginList) {
						if (plugin.hash == fileHash) {
							pluginLoaded = true;
							break;
						}
					}

					if (!pluginLoaded) {
						MultiplayerUtils.ExtractZipContent(file, pluginDirectory + "\\");
						
						foreach (string dir in Directory.GetDirectories(pluginDirectory)) {
							if (!pluginDirectories.Contains(dir)) {
								directoryToHash.Add(Tuple.Create(dir, fileHash));
								pluginDirectories.Add(dir);
								UnityModManager.Logger.Log($"New plugin {dir}");
								break;
							}
						}
					}
				}
			}

			foreach (string folder in Directory.GetDirectories(pluginDirectory)) {
				if(!Main.pluginList.Where(p => p.path == folder).Any()) {
					string infoFile = Path.Combine(folder, "Info.json");
					if (File.Exists(infoFile)) {
						Plugin newPlugin = JsonConvert.DeserializeObject<Plugin>(File.ReadAllText(infoFile));
						UnityModManager.Logger.Log(newPlugin.path);
						pluginList.Add(new Plugin(newPlugin.dllName, newPlugin.startMethod, folder, SendMessageFromPlugin));

						foreach (Tuple<string, string> hashDirPair in directoryToHash) {
							if (hashDirPair.Item1 == folder) {
								Traverse.Create(pluginList[pluginList.Count - 1]).Property("hash").SetValue(hashDirPair.Item2);
							}
						}
					}
				}
			}

			foreach (Plugin plugin in pluginList) {
				if (plugin.loadedDLL == null) {
					// TODO: Add log statements
					string targetDLLFile = Path.Combine(plugin.path, plugin.dllName);
					if (!File.Exists(targetDLLFile)) continue;
					byte[] dllContents = File.ReadAllBytes(targetDLLFile);
					var loadedDLL = Assembly.Load(dllContents);

					Traverse.Create(plugin).Property("loadedDLL").SetValue(loadedDLL);

					if (loadedDLL != null) {
						MethodInfo entryMethod = AccessTools.Method(plugin.startMethod);

						if (entryMethod != null) {
							try {
								//new object[] { this }
								entryMethod.Invoke(null, new object[] { plugin });
							} catch (Exception e) { }
						} else { }
					} else { }
				}
			}
		}

		private static void SendMessageFromPlugin(Plugin source, byte[] message, bool reliable) {
			if (multiplayerController != null) {
				byte[] sendMessage = new byte[message.Length + 2];
				sendMessage[0] = (byte)OpCode.Plugin;
				sendMessage[1] = source.pluginID;

				Array.Copy(message, 0, sendMessage, 2, message.Length);

				multiplayerController.SendBytesRaw(sendMessage, reliable, false, false, message.Length > 1024);
			}
		}

		// TODO: Move to utils class
		public static void ClearDirectory(string dir) {
			foreach (string file in Directory.GetFiles(dir)) {
				File.Delete(file);
			}

			foreach (string subdir in Directory.GetDirectories(dir)) {
				ClearDirectory(subdir);
			}

			Directory.Delete(dir);
		}

		public static IEnumerator StartUpdatingServerList() {
			while (true) {
				if (NewMultiplayerMenu.Instance != null && NewMultiplayerMenu.Instance.serverBrowserMenu.activeSelf) {
					UnityWebRequest www = UnityWebRequest.Get("http://www.davisellwood.com/api/getservers/");
					yield return www.SendWebRequest();

					if (www.isNetworkError || www.isHttpError) {
						yield return new WaitForSeconds(5);
					} else {
						foreach (RectTransform trans in NewMultiplayerMenu.Instance.serverItems) {
							GameObject.Destroy(trans.gameObject);
						}

						yield return new WaitForEndOfFrame();

						var responseString = www.downloadHandler.text;
						responseString = responseString.Remove(0, 1).Remove(responseString.Length - 2, 1).Replace("\\\"", "\"");

						JArray a = JArray.Parse(responseString);

						while (NewMultiplayerMenu.Instance.serverItems.Count > 0) {
							bool allDestroyed = true;
							foreach (RectTransform trans in NewMultiplayerMenu.Instance.serverItems) {
								if (trans != null) {
									allDestroyed = false;
									break;
								}
							}

							if (allDestroyed) {
								NewMultiplayerMenu.Instance.serverItems.Clear();
								break;
							} else {
								yield return new WaitForEndOfFrame();
							}
						}

						foreach (JObject o in a.Children<JObject>()) {
							foreach (JProperty p in o.Properties()) {
								if (p.Name == "fields") {
									Server newServer = new Server();
									foreach (JObject o2 in p.Children<JObject>()) {
										foreach (JProperty p2 in o2.Properties()) {
											switch (p2.Name.ToLower()) {
												case "name":
													newServer.name = (string)p2.Value;
													break;
												case "ip":
													newServer.ip = (string)p2.Value;
													break;
												case "port":
													newServer.port = (string)p2.Value;
													break;
												case "version":
													newServer.version = (string)p2.Value;
													break;
												case "maxplayers":
													newServer.playerMax = (int)p2.Value;
													break;
												case "currentplayers":
													newServer.playerCurrent = (int)p2.Value;
													break;
												case "mapname":
													newServer.mapName = (string)p2.Value;
													break;
											}
										}
									}
									NewMultiplayerMenu.Instance.AddServerItem(newServer.ip, newServer.port, newServer.name, newServer.mapName, newServer.version, $"{newServer.playerCurrent} / {newServer.playerMax}", ClickServerItem);
								}
							}
						}
						yield return new WaitForSeconds(30);
					}
				} else {
					yield return new WaitForSeconds(1);
				}
			}
		}

		static Stopwatch serverClickWatch = new Stopwatch();

		private static void ClickServerItem(ServerListItem target) {
			if(!serverClickWatch.IsRunning || serverClickWatch.Elapsed.TotalMilliseconds > 1000f) {
				if (target.ServerVersion.text.Trim().ToLower().Equals(modEntry.Version.ToString())) {
					UnityModManager.Logger.Log($"Attempting to connect to server {target.ServerName.text} with ip {target.ipAddress} port {target.port}");
					JoinServer(target.ipAddress, target.port, NewMultiplayerMenu.Instance.usernameFields[0].text);
					serverClickWatch.Restart();
				} else {
					utilityMenu.SendImportantChat($"<color=#f22><b>Unable to connect to server because it's version {target.ServerVersion.text} does not match client version {modEntry.Version.ToString()}</b></color>", 15000);
				}
			}
		}

		private static void OnClickConnect() {
			JoinServer(NewMultiplayerMenu.Instance.connectMenu.transform.Find("IP Address").GetComponent<InputField>().text, NewMultiplayerMenu.Instance.connectMenu.transform.Find("Port").GetComponent<InputField>().text, NewMultiplayerMenu.Instance.usernameFields[1].text);
		}

		private static void JoinServer(string ip, string port, string username) {
			if (Main.multiplayerController == null) {
				NewMultiplayerMenu.Instance.mainMenuObject.SetActive(false); ;
				Cursor.visible = false;
				multiplayerController = new GameObject().AddComponent<MultiplayerController>();
				GameObject.DontDestroyOnLoad(multiplayerController.gameObject);

				username = username.Trim().Equals("") ? "Username" : username;

				PreviousUsername previousUsername = new PreviousUsername();
				if (File.Exists(Main.modEntry.Path + "\\PreviousName.json")) {
					previousUsername = JsonConvert.DeserializeObject<PreviousUsername>(File.ReadAllText(Main.modEntry.Path + "\\PreviousName.json"));
				}
				previousUsername.username = username;

				string newFile = JsonConvert.SerializeObject(previousUsername);
				File.WriteAllText(Main.modEntry.Path + "\\PreviousName.json", newFile);

				Main.multiplayerController.ConnectToServer(ip.Equals("") ? "127.0.0.1" : ip, (ushort)(port.Equals("") ? 7777 : int.Parse(port)), username);
			}
		}

		public static void OnClickDisconnect() {
			if (Main.multiplayerController != null) {
				GameObject.Destroy(Main.multiplayerController);
				NewMultiplayerMenu.Instance.mainMenuObject.SetActive(false);
				Cursor.visible = false;
			}
		}

		// TODO: Move this to another file
		private static void MenuUpdate() {
			if (NewMultiplayerMenu.Instance != null) {
				if (Input.GetKeyDown(KeyCode.P)) {
					NewMultiplayerMenu.Instance.serverBrowserMenu.SetActive(false);
					NewMultiplayerMenu.Instance.connectMenu.SetActive(false);
					
					NewMultiplayerMenu.Instance.mainMenuObject.SetActive(!NewMultiplayerMenu.Instance.mainMenuObject.activeSelf);

					if (NewMultiplayerMenu.Instance.mainMenuObject.activeSelf) {
						if (MultiplayerUtils.hashedMaps != LevelManager.Instance.CustomLevels.Count)
							MultiplayerUtils.StartMapLoading();
						Cursor.visible = true;
						Cursor.lockState = CursorLockMode.None;
						if (Main.multiplayerController != null) {
							NewMultiplayerMenu.Instance.directConnectButton.SetActive(false);
							NewMultiplayerMenu.Instance.serverBrowserButton.SetActive(false);
							NewMultiplayerMenu.Instance.disconnectButton.SetActive(true);
						} else {
							NewMultiplayerMenu.Instance.directConnectButton.SetActive(true);
							NewMultiplayerMenu.Instance.serverBrowserButton.SetActive(true);
							NewMultiplayerMenu.Instance.disconnectButton.SetActive(false);

							PreviousUsername previousUsername = new PreviousUsername();

							if (File.Exists(Main.modEntry.Path + "\\PreviousName.json")) {
								previousUsername = JsonConvert.DeserializeObject<PreviousUsername>(File.ReadAllText(Main.modEntry.Path + "\\PreviousName.json"));
							}

							if (previousUsername.username != "") {
								foreach (InputField usernameField in NewMultiplayerMenu.Instance.usernameFields) {
									usernameField.text = previousUsername.username;
								}
							}

						}
					} else {
						Cursor.visible = false;
					}
				}
			}
		}

		private static void SaveSettings(float volumeMultiplier) {
			Main.settings.volumeMultiplier = volumeMultiplier;
			Main.settings.Save(Main.modEntry);
		}
	}

	[HarmonyPatch(typeof(ReplayEditorController), "OnDisable")]
	static class MultiplayerReplayDisablePatch {
		static void Prefix() {
			if (Main.multiplayerController != null) {
				foreach (MultiplayerRemotePlayerController controller in Main.multiplayerController.remoteControllers) {
					if (controller.playerID != 255) {
						controller.skater.SetActive(true);
						controller.player.SetActive(true);
						controller.usernameObject.SetActive(true);
					}
				}
			} else {
				Main.debugWriter.Flush();
				Main.debugWriter.Close();
			}

			if (Main.remoteReplayControllers != null) {
				foreach (MultiplayerRemotePlayerController controller in Main.remoteReplayControllers) {
					controller.Destroy();
				}
				Main.remoteReplayControllers.Clear();
			}
		}
	}

	[HarmonyPatch(typeof(ReplayPlaybackController), "Update")]
	static class MultiplayerPlaybackUpdatePatch {
		static bool Prefix(ReplayPlaybackController __instance) {
			return __instance == ReplayEditorController.Instance.playbackController || !GameManagement.GameStateMachine.Instance.CurrentState.GetType().Equals(typeof(GameManagement.ReplayState));
		}
	}

	[HarmonyPatch(typeof(ReplayEditorController), "Update")]
	static class MultiplayerReplayUpdatePatch {
		static void Postfix(ReplayEditorController __instance) {
			foreach (MultiplayerRemotePlayerController controller in Main.remoteReplayControllers) {
				controller.replayController.TimeScale = ReplayEditorController.Instance.playbackController.TimeScale;
				controller.replayController.SetPlaybackTime(ReplayEditorController.Instance.playbackController.CurrentTime);
			}
		}
	}

	[HarmonyPatch(typeof(ReplayEditorController), "LoadFromFile")]
	static class LoadMultiplayerReplayPatch {
		static void Prefix(string path) {
			string multiplayerReplayFile = Path.GetDirectoryName(path) + "\\" + Path.GetFileNameWithoutExtension(path) + "\\";
			if (Directory.Exists(multiplayerReplayFile)) {
				if (Main.multiplayerController != null && Main.multiplayerController.debugWriter != null) Main.debugWriter = Main.multiplayerController.debugWriter;
				else Main.debugWriter = new StreamWriter("Multiplayer Replay DebugWriter.txt");
				using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(multiplayerReplayFile + "MultiplayerReplay.replay"))) {
					if (CustomFileReader.HasSignature(ms, "SXLDF001")) {
						using (CustomFileReader fileReader = new CustomFileReader(ms)) {
							ReplayPlayerData playerData = null;
							PlayerDataInfo playerDataInfo = null;
							int i = 0;
							do {
								if (fileReader.TryGetData<ReplayPlayerData, PlayerDataInfo>("player" + i.ToString(), out playerData, out playerDataInfo)) {
									Main.remoteReplayControllers.Add(new MultiplayerRemotePlayerController(Main.debugWriter));
									Main.remoteReplayControllers[i].ConstructPlayer();
									Main.remoteReplayControllers[i].characterCustomizer.LoadCustomizations(playerData.customizations);
									List<ReplayRecordedFrame> recordedFrames = new List<ReplayRecordedFrame>(playerData.recordedFrames);
									Main.remoteReplayControllers[i].recordedFrames = recordedFrames;
									Main.remoteReplayControllers[i].FinalizeReplay(false);
								}
								i++;
							} while (playerData != null);
						}
					}
				}
			}

			if (Main.multiplayerController != null) {
				foreach (MultiplayerRemotePlayerController controller in Main.multiplayerController.remoteControllers) {
					controller.skater.SetActive(false);
					controller.player.SetActive(false);
					controller.usernameObject.SetActive(false);
				}
			}
		}
	}

	[HarmonyPatch(typeof(SaveManager), "SaveReplay")]
	static class SaveMultiplayerReplayPatch {
		static void Postfix(string fileID, byte[] data) {
			string replaysPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\SkaterXL";
			if (!Directory.Exists(replaysPath)) {
				Directory.CreateDirectory(replaysPath);
			}
			if (!Directory.Exists(replaysPath + "\\Replays")) {
				Directory.CreateDirectory(replaysPath + "\\Replays");
			}
			replaysPath += "\\Replays\\" + fileID;
			Directory.CreateDirectory(replaysPath);

			// TODO: Thread this shit, parallel as fuck boi
			// TODO: Refactor, I'm reusing lots of code that doesn't need to be repeated

			if (Main.remoteReplayControllers.Count > 0) {
				for (int i = 0; i < Main.remoteReplayControllers.Count; i++) {
					MultiplayerRemotePlayerController remoteController = Main.remoteReplayControllers[i];

					foreach (ReplayRecordedFrame frame in remoteController.replayController.ClipFrames) {
						frame.time -= ReplayEditorController.Instance.playbackController.ClipFrames[0].time;
					}

					foreach (ClothingGearObjet clothingPiece in remoteController.gearList) {
						if (clothingPiece.gearInfo.isCustom) {
							foreach (TextureChange change in clothingPiece.gearInfo.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}

					foreach (BoardGearObject boardPiece in remoteController.boardGearList) {
						if (boardPiece.gearInfo.isCustom) {
							foreach (TextureChange change in boardPiece.gearInfo.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}

					if (remoteController.currentBody.gearInfo.isCustom) {
						foreach (MaterialChange matChange in remoteController.currentBody.gearInfo.materialChanges) {
							foreach (TextureChange change in matChange.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}
				}

				byte[] result;

				using (MemoryStream memoryStream = new MemoryStream()) {
					using (CustomFileWriter customFileWriter = new CustomFileWriter(memoryStream, "SXLDF001")) {
						for (int i = 0; i < Main.remoteReplayControllers.Count; i++) {
							if (Main.remoteReplayControllers[i].replayController.ClipFrames.Count > 0) {
								List<ReplayRecordedFrame> recordedFrames = Main.remoteReplayControllers[i].replayController.ClipFrames;
								ReplayPlayerData replayData = new ReplayPlayerData(recordedFrames.ToArray(), new List<GPEvent>().ToArray(), null, null, null, null, null, null, null, null, null, null, Main.remoteReplayControllers[i].characterCustomizer.CurrentCustomizations);
								PlayerDataInfo dataInfo2 = new PlayerDataInfo("Player" + i.ToString());
								replayData.customizations = Main.remoteReplayControllers[i].characterCustomizer.CurrentCustomizations;
								customFileWriter.AddData(replayData, "player" + i.ToString(), dataInfo2);
								customFileWriter.Write();
							}
						}
						result = memoryStream.ToArray();
					}
				}

				File.WriteAllBytes(replaysPath + "\\MultiplayerReplay.replay", result);
			} else if (Main.multiplayerController != null && Main.remoteReplayControllers.Count == 0) {
				for (int i = 0; i < Main.multiplayerController.remoteControllers.Count; i++) {
					MultiplayerRemotePlayerController remoteController = Main.multiplayerController.remoteControllers[i];

					foreach (ReplayRecordedFrame frame in remoteController.replayController.ClipFrames) {
						frame.time -= ReplayEditorController.Instance.playbackController.ClipFrames[0].time;
					}

					foreach (ClothingGearObjet clothingPiece in remoteController.gearList) {
						if (clothingPiece.gearInfo.isCustom) {
							foreach (TextureChange change in clothingPiece.gearInfo.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}

					foreach (BoardGearObject boardPiece in remoteController.boardGearList) {
						if (boardPiece.gearInfo.isCustom) {
							foreach (TextureChange change in boardPiece.gearInfo.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}

					if (remoteController.currentBody.gearInfo.isCustom) {
						foreach (MaterialChange matChange in remoteController.currentBody.gearInfo.materialChanges) {
							foreach (TextureChange change in matChange.textureChanges) {
								if (change.textureID.ToLower().Equals("albedo")) {
									string newPath = replaysPath + "\\" + Path.GetFileName(change.texturePath);
									File.Copy(change.texturePath, newPath);
									change.texturePath = newPath;
								}
							}
						}
					}
				}

				byte[] result;

				using (MemoryStream memoryStream = new MemoryStream()) {
					using (CustomFileWriter customFileWriter = new CustomFileWriter(memoryStream, "SXLDF001")) {
						for (int i = 0; i < Main.multiplayerController.remoteControllers.Count; i++) {
							if (Main.multiplayerController.remoteControllers[i].replayController.ClipFrames.Count > 0) {
								List<ReplayRecordedFrame> recordedFrames = Main.multiplayerController.remoteControllers[i].replayController.ClipFrames;
								ReplayPlayerData replayData = new ReplayPlayerData(recordedFrames.ToArray(), new List<GPEvent>().ToArray(), null, null, null, null, null, null, null, null, null, null, Main.multiplayerController.remoteControllers[i].characterCustomizer.CurrentCustomizations);
								PlayerDataInfo dataInfo2 = new PlayerDataInfo("Player" + i.ToString());
								replayData.customizations = Main.multiplayerController.remoteControllers[i].characterCustomizer.CurrentCustomizations;
								customFileWriter.AddData(replayData, "player" + i.ToString(), dataInfo2);
								customFileWriter.Write();
							}
						}
						result = memoryStream.ToArray();
					}
				}

				File.WriteAllBytes(replaysPath + "\\MultiplayerReplay.replay", result);
			}
		}
	}

	[HarmonyPatch(typeof(ReplayPlaybackController), "CutClip")]
	static class CutClipMultiplayerReplayPatch {
		static void Prefix(ReplayPlaybackController __instance, float newStartTime, float newEndTime) {
			if (Main.remoteReplayControllers.Find(c => c.replayController == __instance) != null || (Main.multiplayerController != null && Main.multiplayerController.remoteControllers.Find(c => c.replayController == __instance) != null)) return;

			if (Main.multiplayerController != null) {
				foreach (MultiplayerRemotePlayerController controller in Main.multiplayerController.remoteControllers) {
					if (controller.replayController == null || controller.replayController.ClipFrames == null) {
						continue;
					} else if (controller.replayController.ClipFrames.Count > 0 && (controller.replayController.ClipFrames.Last().time <= newStartTime || controller.replayController.ClipFrames.First().time >= newEndTime)) {
						controller.replayController.ClipFrames.Clear();
					} else if (controller.replayController != null && controller.replayController.ClipFrames != null && controller.replayController.ClipFrames.Count > 0) {
						int framesToRemove = 0;
						while (framesToRemove < controller.replayController.ClipFrames.Count && controller.replayController.ClipFrames[framesToRemove].time < newStartTime) {
							framesToRemove++;
						}
						controller.replayController.ClipFrames.RemoveRange(0, framesToRemove);

						if (controller.replayController.ClipFrames.Count > 0) {
							framesToRemove = 0;
							while (framesToRemove < controller.replayController.ClipFrames.Count && controller.replayController.ClipFrames[controller.replayController.ClipFrames.Count - 1 - framesToRemove].time > newEndTime) {
								framesToRemove++;
							}
							controller.replayController.ClipFrames.RemoveRange(controller.replayController.ClipFrames.Count - framesToRemove, framesToRemove);
						}

						if (controller.replayController.ClipFrames.Count > 0) {
							controller.replayController.ClipFrames.ForEach(delegate (ReplayRecordedFrame f) {
								f.time -= newStartTime;
							});

							controller.replayController.ClipEndTime = newEndTime - newStartTime;
							controller.replayController.CurrentTime = Mathf.Clamp(controller.replayController.CurrentTime - newStartTime, 0f, controller.replayController.ClipEndTime);

							controller.replayController.StartCoroutine("UpdateAnimationClip");
						}
					}
				}
			}
			foreach (MultiplayerRemotePlayerController controller in Main.remoteReplayControllers) {
				if (controller.replayController == null || controller.replayController.ClipFrames == null) {
					continue;
				} else if (controller.replayController.ClipFrames.Count > 0 && (controller.replayController.ClipFrames.Last().time <= newStartTime || controller.replayController.ClipFrames.First().time >= newEndTime)) {
					Main.multiplayerController.remoteControllers.Remove(controller);
					controller.Destroy();
				} else if (controller.replayController != null && controller.replayController.ClipFrames != null && controller.replayController.ClipFrames.Count > 0) {
					int framesToRemove = 0;
					while (framesToRemove < controller.replayController.ClipFrames.Count && controller.replayController.ClipFrames[framesToRemove].time < newStartTime) {
						framesToRemove++;
					}
					controller.replayController.ClipFrames.RemoveRange(0, framesToRemove);

					if (controller.replayController.ClipFrames.Count > 0) {
						framesToRemove = 0;
						while (framesToRemove < controller.replayController.ClipFrames.Count && controller.replayController.ClipFrames[controller.replayController.ClipFrames.Count - 1 - framesToRemove].time > newEndTime) {
							framesToRemove++;
						}
						controller.replayController.ClipFrames.RemoveRange(controller.replayController.ClipFrames.Count - framesToRemove, framesToRemove);
					}

					if (controller.replayController.ClipFrames.Count > 0) {
						controller.replayController.ClipFrames.ForEach(delegate (ReplayRecordedFrame f) {
							f.time -= newStartTime;
						});

						controller.replayController.ClipEndTime = newEndTime - newStartTime;
						controller.replayController.CurrentTime = Mathf.Clamp(controller.replayController.CurrentTime - newStartTime, 0f, controller.replayController.ClipEndTime);

						controller.replayController.StartCoroutine("UpdateAnimationClip");
					}
				}
			}
		}
	}

	[HarmonyPatch(typeof(AudioSource), "PlayOneShot", new Type[] { typeof(AudioClip), typeof(float) })]
	static class PlayOneShotPatch {
		private static List<AudioSource> localAudioSources = new List<AudioSource>();
		private static bool Prefix(AudioSource __instance, ref float volumeScale) {
			bool isLocal = false;

			if (localAudioSources.Count < 1) {
				foreach (ReplayAudioEventPlayer player in ReplayEditorController.Instance.playbackController.AudioEventPlayers) {
					localAudioSources.Add(Traverse.Create(player).Property("audioSource").GetValue<AudioSource>());
				}
			}
			foreach(AudioSource audioSource in localAudioSources) {
				if (__instance == audioSource) {
					isLocal = true;
					break;
				}
			}

			if (!isLocal) volumeScale *= Main.settings.volumeMultiplier;

			return true;
		}
	}

	[HarmonyPatch(typeof(ReplayAudioEventPlayer), "DoVolumeEventAt")]
	static class VolumeEventPatch {
		private static bool Prefix(ReplayAudioEventPlayer __instance, int index, ref int ___lastVolumeEventIndex, ref AudioSource ___m_AudioSource) {
			if (ReplayEditorController.Instance.playbackController.AudioEventPlayers.Contains(__instance)) {
				return true;
			}

			___lastVolumeEventIndex = index;
			AudioVolumeEvent audioVolumeEvent = __instance.volumeEvents[index];
			___m_AudioSource.volume = audioVolumeEvent.volume * Main.settings.volumeMultiplier;

			return false;
		}
	}

	[HarmonyPatch(typeof(ReplayListViewController), "OnItemSelected")]
	static class ReplayLoadFromFilePatch {
		static bool Prefix() {
			return Main.multiplayerController == null;
		}
	}

	[HarmonyPatch(typeof(LevelSelectionController), "Update")]
	static class LevelSelectionControllerUpdatePatch {
		static bool Prefix() {
			return Main.multiplayerController == null || !Main.multiplayerController.isConnected;
		}
	}

	[HarmonyPatch(typeof(PlayTime))]
	[HarmonyPatch("time", MethodType.Getter)]
	class PlayTime_timePatch {
		public static bool Prefix(ref float __result) {
			__result = Time.time;
			return false;
		}
	}

	[HarmonyPatch(typeof(Input), "GetKeyDown", typeof(KeyCode))]
	static class InputKeyDownPatch {
		static void Postfix(ref bool __result) {
			if (NewMultiplayerMenu.Instance != null && NewMultiplayerMenu.Instance.IsFocusedInput()) __result = false;
		}
	}

	[HarmonyPatch(typeof(Input), "GetKeyUp", typeof(KeyCode))]
	static class InputKeyUpPatch {
		static void Postfix(ref bool __result) {
			if (NewMultiplayerMenu.Instance != null && NewMultiplayerMenu.Instance.IsFocusedInput()) __result = false;
		}
	}
}