KK_PerspectiveX

這是一款適用於《Koikatsu》的第一人稱視角插件，不會讓攝影機穿進角色身體，也不容易造成暈眩。

可用於主遊戲的自由移動模式、H 場景，以及 CharaStudio。

同時支援 Koikatsu 與 Koikatsu Sunshine，兩款遊戲共用同一個 DLL 檔案。我已經在 Sunshine 中進行過完整測試，目前的運作表現與原版 Koikatsu 幾乎相同。不過，由於 Sunshine 是較新且較少見的第一人稱插件支援目標，偶爾仍可能遇到 Sunshine 專屬的錯誤。

如果未來兩款遊戲的差異變得更大，可能需要將插件拆分成不同版本，但目前同一個 DLL 就能同時支援兩款遊戲。

由 Tokozakura 維護的繁體中文 ZH-TW 分支版本可在此取得：版本下載。

為什麼還需要另一款第一人稱視角插件？

目前已有的第一人稱視角插件主要有兩個問題。

部分插件會把攝影機放在錯誤的位置，導致視角位於胸口高度，而且角色頭部會穿進畫面。

RealPOV 雖然能正確放置攝影機，但它會完整複製頭部骨骼的動畫旋轉，其中也包含左右傾斜，因此畫面會隨著每個動畫動作左右歪斜、突然晃動。

PerspectiveX 會將攝影機的位置與旋轉分開處理。

以上內容是根據閱讀 RealPOV 與 KK_StudioPOV 的公開原始碼所整理，只是在說明程式碼的實際運作方式，並不是在貶低任何一個專案。

安裝方式
需要安裝 BepInEx 5，HF Patch 已經內建。
從 Releases 下載 KK_PerspectiveX.dll，並將檔案放入：
BepInEx/plugins/
如果你已經安裝 RealPOV，我認為 HF Patch 可能有內建它，請將：
RealPOV.Koikatu.dll

重新命名為：

RealPOV.Koikatu.dll.disabled

這樣可以停用 RealPOV，因為兩款插件都使用 Backspace 作為第一人稱視角切換鍵。

已在 Koikatsu 與 Koikatsu Sunshine 搭配 BepInEx 5 以上版本進行測試與遊玩。

如果在你的遊戲環境中無法正常使用，請建立問題回報，我會查看問題。

操作方式

所有按鍵都可以透過 ConfigurationManager，預設按 F1 開啟，進行重新設定。

Backspace：開啟或關閉第一人稱視角。
在 Studio 中，必須先在工作區選擇一名角色。
按住滑鼠左鍵並拖曳：轉動視角。
未拖曳時，滑鼠游標仍會保持顯示並可正常操作。
Ctrl + L：啟用免按住滑鼠的 FPS 視角控制。
滑鼠游標會被鎖定，直到再次按下 Ctrl + L。
Ctrl + Shift + 左方向鍵／右方向鍵：
將第一人稱視角切換至上一名或下一名角色。
滑鼠滾輪：在第一人稱視角中調整 FOV。
如果經常不小心碰到滾輪，可在設定中關閉此功能。
逗號鍵／句號鍵：讓攝影機向左或向右傾斜。
斜線鍵可將傾斜角度重設為水平。
分號鍵：將攝影機鎖定在目前位置。
鎖定後，視角不會再跟隨頭部移動，特別適合用於會讓頭部大幅晃動的愛撫動畫。鎖定期間仍然可以自由轉動視角。再次按下後即可解除鎖定，攝影機會平滑移回角色頭部位置。
Ctrl + Shift + 1／2／3：
將目前視角儲存至指定欄位，包含 FOV、觀看方向、傾斜角度與攝影機偏移。

Ctrl + 1／2／3：
載入對應欄位中的已儲存視角。

儲存欄位會保留，即使關閉遊戲後再次啟動也不會消失。

Ctrl + P：開啟預設面板。
在 Studio 中，也可以點擊左下角工具列中的 PerspectiveX 眼睛按鈕。
預設面板

遊戲內面板會將所有與預設相關的功能集中在同一個位置，不需要再進入插件設定中尋找。

視角預設：
提供一鍵套用的設定組合，包括：

舒適 60
自然 90
動作 110

這些預設會同時調整 FOV、位置平滑與攝影機偏移。

自訂預設：
可以自行調整喜歡的 FOV、平滑程度與偏移組合，輸入名稱後儲存成自己的單鍵預設，最多可儲存 5 組。
已儲存視角：
與快捷鍵相同的 3 個儲存與載入欄位，會以按鈕顯示，並顯示每個欄位儲存的 FOV。

舒適性設定，包括位置平滑、動畫晃動、FOV 與攝影機偏移，都可以在插件設定中調整，並且能在第一人稱模式中即時套用。

視角預設也能直接在插件設定中使用。

另有一個可選設定：

「攝影機對齊身體」

啟用後，攝影機會配合角色身體的方向傾斜。例如角色側躺時，畫面也會跟著身體方向傾斜，而不是強制保持地平線水平。

從原始碼建置

本插件以 .NET Framework 3.5 為目標框架，可以在任何作業系統上使用一般的 .NET SDK 進行編譯。

首先，請從遊戲安裝目錄中複製以下參考 DLL 檔案至專案的 lib/ 資料夾。

由於這些檔案受著作權保護，因此不會包含在原始碼專案中。

從以下資料夾：

BepInEx/core/

複製：

BepInEx.dll
0Harmony.dll

從以下資料夾：

Koikatu_Data/Managed/

複製：

Assembly-CSharp.dll
Assembly-CSharp-firstpass.dll
UnityEngine.dll
UnityEngine.UI.dll

接著執行：

cd src/KK_PerspectiveX
dotnet build -c Release

編譯完成後，輸出檔案位於：

src/KK_PerspectiveX/bin/Release/KK_PerspectiveX.dll
