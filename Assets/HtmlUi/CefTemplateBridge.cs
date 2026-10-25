using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Small reusable bridge for the Unity CEF template.
/// It loads a StreamingAssets HTML page, receives JSON messages from JavaScript,
/// and sends JSON messages back through HtmlUiView.
/// </summary>
[RequireComponent(typeof(HtmlUiView))]
public sealed class CefTemplateBridge : MonoBehaviour
{
    [Tooltip("StreamingAssets-relative HTML page loaded into CEF.")]
    public string initialPage = "ui/index.html";

    [Tooltip("Seconds between heartbeat messages sent to JavaScript. Set to 0 to disable.")]
    public float heartbeatIntervalSeconds = 1.0f;

    private HtmlUiView htmlUi;
    private float heartbeatTimer;
    private int heartbeatSequence;
    private bool readySent;

    private void Awake()
    {
        htmlUi = GetComponent<HtmlUiView>();
    }

    private void Start()
    {
        htmlUi.OnMessage += HandleMessageFromHtml;
        htmlUi.LoadStreamingAsset(initialPage);
        StartCoroutine(SendReadyAfterStartup());
    }

    private void Update()
    {
        if (heartbeatIntervalSeconds <= 0f)
        {
            return;
        }

        heartbeatTimer += Time.unscaledDeltaTime;
        if (heartbeatTimer < heartbeatIntervalSeconds)
        {
            return;
        }

        heartbeatTimer = 0f;
        Send(new TemplateTickMessage
        {
            sequence = ++heartbeatSequence,
            realtimeSinceStartup = Time.realtimeSinceStartup
        });
    }

    private void OnDestroy()
    {
        if (htmlUi != null)
        {
            htmlUi.OnMessage -= HandleMessageFromHtml;
        }
    }

    private void HandleMessageFromHtml(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        Debug.Log("CefTemplateBridge received: " + json);

        BridgeRequest request;
        try
        {
            request = JsonUtility.FromJson<BridgeRequest>(json);
        }
        catch (Exception ex)
        {
            SendStatus("Invalid JSON from HTML: " + ex.Message, "error");
            return;
        }

        if (request == null || string.IsNullOrEmpty(request.type))
        {
            SendStatus("HTML message did not include a type.", "warning");
            return;
        }

        switch (request.type)
        {
            case "cef-template-ping":
                SendReady();
                Send(new TemplatePongMessage
                {
                    text = string.IsNullOrEmpty(request.text) ? "pong" : request.text,
                    realtimeSinceStartup = Time.realtimeSinceStartup
                });
                break;

            case "cef-template-echo":
            case "cef-template-command":
                Send(new TemplateEchoMessage
                {
                    text = string.IsNullOrEmpty(request.text) ? request.command : request.text,
                    command = request.command
                });
                break;

            case "button-click":
                SendStatus("Button click received by Unity at " + Time.realtimeSinceStartup.ToString("F2") + "s.", "info");
                break;

            case "html-pointer-block":
                break;

            default:
                SendStatus("Unhandled HTML message type: " + request.type, "warning");
                break;
        }
    }

    private void SendReady()
    {
        readySent = true;
        Send(new TemplateReadyMessage
        {
            appName = Application.productName,
            unityVersion = Application.unityVersion,
            platform = Application.platform.ToString(),
            realtimeSinceStartup = Time.realtimeSinceStartup
        });
    }

    private IEnumerator SendReadyAfterStartup()
    {
        yield return null;
        yield return null;
        yield return new WaitForSecondsRealtime(0.25f);

        if (!readySent)
        {
            SendReady();
        }
    }

    private void SendStatus(string text, string severity)
    {
        Send(new TemplateStatusMessage
        {
            text = text,
            severity = severity,
            realtimeSinceStartup = Time.realtimeSinceStartup
        });
    }

    private void Send<T>(T message)
    {
        if (htmlUi == null)
        {
            return;
        }

        htmlUi.SendToJavaScript(JsonUtility.ToJson(message));
    }

    [Serializable]
    private sealed class BridgeRequest
    {
        public string type;
        public string text;
        public string command;
    }

    [Serializable]
    private sealed class TemplateReadyMessage
    {
        public string type = "cef-template-ready";
        public string appName;
        public string unityVersion;
        public string platform;
        public float realtimeSinceStartup;
    }

    [Serializable]
    private sealed class TemplateStatusMessage
    {
        public string type = "cef-template-status";
        public string text;
        public string severity;
        public float realtimeSinceStartup;
    }

    [Serializable]
    private sealed class TemplatePongMessage
    {
        public string type = "cef-template-pong";
        public string text;
        public float realtimeSinceStartup;
    }

    [Serializable]
    private sealed class TemplateEchoMessage
    {
        public string type = "cef-template-echo-response";
        public string text;
        public string command;
    }

    [Serializable]
    private sealed class TemplateTickMessage
    {
        public string type = "cef-template-tick";
        public int sequence;
        public float realtimeSinceStartup;
    }
}
