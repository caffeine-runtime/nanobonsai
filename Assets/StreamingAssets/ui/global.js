/**
 * Unity bridge for pages shown by HtmlUiView.
 *
 * UnityBridge.send(message) reaches C# as HtmlUiView.OnMessage (a JSON string); a string passed to
 * HtmlUiView.SendToJavaScript(json) arrives, parsed, at every UnityBridge.onMessage callback.
 * sendToUnity(type, payload) sends { type, ...payload }. escapeHtml(value) makes text safe for innerHTML.
 */

(function() {
  // The bridge exists only where HtmlUiView loads pages, from the file system.
  if (window.location.protocol === 'file:' && !window.UnityBridge) {
    window.UnityBridge = {
      _callbacks: [],
      // The native bridge forwards console lines prefixed "unity:" to C#.
      send(message) {
        console.log('unity:' + JSON.stringify(message));
      },
      onMessage(callback) {
        this._callbacks.push(callback);
      },
      _receive(messageText) {
        let message = messageText;
        if (typeof messageText === 'string') {
          try {
            message = JSON.parse(messageText);
          } catch (e) {
            console.error('UnityBridge parse error:', e);
            return;
          }
        }
        this._callbacks.forEach(cb => {
          try {
            cb(message);
          } catch (err) {
            console.error('UnityBridge callback error:', err);
          }
        });
      }
    };
  }

  window.sendToUnity = function(type, payload = {}) {
    if (window.UnityBridge) {
      window.UnityBridge.send({ type, ...payload });
    }
  };

  window.escapeHtml = function(str) {
    return String(str == null ? '' : str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  };
})();
