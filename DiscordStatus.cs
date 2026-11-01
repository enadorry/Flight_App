using System;
using UnityEngine;

public class DiscordStatus : MonoBehaviour
{
    [Header("Discord Developer PortalのApplication ID")]
    [SerializeField] private string applicationId = "1565993755138461787";

    [Header("Flight_unityapp")]
    [SerializeField] private string details = "ゲームをプレイ中";
    [SerializeField] private string state = "Flight Now...";

    private Discord.Discord discord;
    private long startTimestamp;

    private void Start()
    {
        // Application IDを数値に変換
        if (!long.TryParse(applicationId, out long id))
        {
            Debug.LogError(
                "Application IDを確認してください。数字だけを入力します。"
            );
            return;
        }

        try
        {
            // Discordが起動していなくてもUnityを実行できるようにする
            discord = new Discord.Discord(
                id,
                (ulong)Discord.CreateFlags.NoRequireDiscord
            );

            // プレイ開始時刻。Game SDKでは秒を使う
            startTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 最初のステータスを表示
            UpdateStatus(details, state);

            Debug.Log("Discord Game SDKを初期化しました。");
        }
        catch (Exception e)
        {
            Debug.LogError("Discordの初期化に失敗しました: " + e.Message);
        }
    }

    private void Update()
    {
        if (discord == null) return;

        try
        {
            // Discordからの応答を処理
            discord.RunCallbacks();
        }
        catch (Exception e)
        {
            Debug.LogError("Discordのコールバックエラー: " + e.Message);
        }
    }

    /// <summary>
    /// Discord上の表示を更新する
    /// </summary>
    public void UpdateStatus(string newDetails, string newState)
    {
        if (discord == null)
        {
            Debug.LogWarning("Discordが初期化されていません。");
            return;
        }

        var activity = new Discord.Activity
        {
            Details = newDetails,
            State = newState,
            Timestamps =
            {
                Start = startTimestamp
            }
        };

        discord.GetActivityManager().UpdateActivity(activity, result =>
        {
            if (result == Discord.Result.Ok)
            {
                Debug.Log("Discordステータスを更新しました！");
            }
            else
            {
                Debug.LogError(
                    "Discordステータス更新に失敗しました: " + result
                );
            }
        });
    }

    private void OnDestroy()
    {
        // Unityの終了時やオブジェクト削除時にSDKを解放
        if (discord != null)
        {
            discord.Dispose();
            discord = null;
        }
    }
}