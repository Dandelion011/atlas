/* =========================================================================
   ColorWitch · SAI2 插件面板 · 嵌入脚本
   -------------------------------------------------------------------------
   用法：宿主加载 index.html?embed=1，页面加载完成后注入（先 embed.css，再
   执行本文件）。本文件不改动页面的任何功能，只做四件事：
     1. 在 <html> 上挂 .cw-embed，让 embed.css 生效；
     2. 量一次命令栏高度，交给 CSS 算内容区（#deck）的可用高度；
     3. 在底部加 #cwBar：应用到 SAI2 / 读取当前色 / 实时同步 / 贴靠 / 状态点；
     4. 接宿主消息桥，并在拖拽取色时按 ≤30Hz 节流发 app:apply。

   原页面的全部模块（读数图、色板、历史、渐变、色卡、L/Y、切面开关、
   四套数值、对比度、导出、保存图片、吸色、复制、链接、操作说明）都保留，
   由页面自己的响应式断点决定 380 宽单列 / 1180 宽三栏。

   没有 ?embed=1 时本文件立即返回，普通打开 index.html 完全不受影响。
   浏览器里没有 chrome.webview 时用 window.__cwHostMock 模拟宿主。

   契约见 plugin/CONTRACT.md。
   ========================================================================= */
(function () {
  'use strict';

  var params = null;
  try { params = new URLSearchParams(window.location.search); } catch (e) { params = null; }
  if (!params || params.get('embed') !== '1') return;

  /* 尽早挂上 class：embed.css 的规则全挂在这下面 */
  function markRoot() {
    if (document.documentElement) document.documentElement.classList.add('cw-embed');
  }
  markRoot();

  function boot() {
    /* 防重复注入：宿主重载页面时 window 会重建，这里只防同一页面注入两次 */
    if (document.getElementById('cwBar')) return;

    var cmd = document.querySelector('header.cmd');
    var cmdHex = document.getElementById('cmdHex');
    var hexIn = document.getElementById('hexIn');
    if (!cmdHex || !hexIn) {
      console.warn('[ColorWitch] 嵌入模式找不到必需的 DOM 节点（index.html 结构变了？）');
      return;
    }

    /* ---- 1. 量命令栏高度：它会在窄宽度换行，内容区高度按实际值算 ---- */
    function measureHeader() {
      var h = cmd ? cmd.getBoundingClientRect().height : 46;
      if (h > 0) document.documentElement.style.setProperty('--cw-cmd-h', Math.round(h) + 'px');
    }
    measureHeader();

    /* ---- 2. 底部操作条 ---- */
    var bar = document.createElement('div');
    bar.id = 'cwBar';
    bar.setAttribute('role', 'toolbar');
    bar.setAttribute('aria-label', 'SAI2 操作');
    bar.innerHTML =
      '<button type="button" id="cwApply" class="cw-apply">应用到 SAI2</button>' +
      '<button type="button" id="cwRead" class="cw-btn" title="让宿主读一次 SAI2 前景色">读取当前色</button>' +
      '<label class="cw-live" title="勾上后拖拽取色时实时写进 SAI2"><input type="checkbox" id="cwLive"><span>实时同步</span></label>' +
      '<span class="cw-spacer"></span>' +
      '<span class="cw-tail">' +
        '<button type="button" id="cwAttach" class="cw-btn cw-attach" title="贴靠到 SAI2 侧边">贴靠</button>' +
        '<span id="cwStatus" class="cw-status" title="宿主桥接：未连接"><i></i></span>' +
      '</span>';
    document.body.appendChild(bar);

    var applyBtn = document.getElementById('cwApply');
    var readBtn = document.getElementById('cwRead');
    var attachBtn = document.getElementById('cwAttach');
    var liveBox = document.getElementById('cwLive');
    var statusEl = document.getElementById('cwStatus');

    /* ---- 3. 颜色桥 ---- */
    var webview = window.chrome && window.chrome.webview;
    var hasWebView = !!(webview && typeof webview.postMessage === 'function');

    function post(msg) {
      if (hasWebView) webview.postMessage(msg);
      else if (window.__cwHostMock) window.__cwHostMock.postMessage(msg);
    }

    function normHex(v) {
      var h = String(v == null ? '' : v).trim().replace(/^#/, '').toUpperCase();
      return /^[0-9A-F]{6}$/.test(h) ? '#' + h : '';
    }
    function currentHex() { return normHex(cmdHex.textContent); }

    /* echo 用来吃掉「宿主推色 → 页面回写」这一圈 */
    var lastHex = currentHex();
    var echo = { hex: '', until: 0 };
    var realtime = liveBox.checked;

    function setStatus(kind) {
      statusEl.classList.remove('ok', 'err', 'na');
      if (kind === 'ok') {
        statusEl.classList.add('ok');
        statusEl.title = '宿主桥接：已连接';
      } else if (kind === 'error') {
        statusEl.classList.add('err');
        statusEl.title = '宿主桥接：错误';
      } else {
        statusEl.classList.add('na');
        statusEl.title = '宿主桥接：未连接';
      }
    }

    var attached = null;
    function renderAttach() {
      attachBtn.textContent = attached === true ? '浮动' : '贴靠';
      attachBtn.title = attached === true ? '改为浮动窗口' : '贴靠到 SAI2 侧边';
    }

    /* 宿主推来的颜色：写进 HEX 输入框并派发 input，页面自己的校验/换算接管 */
    function setColorFromHost(hex) {
      var h = normHex(hex);
      if (!h || h === currentHex()) return;
      echo.hex = h;
      echo.until = Date.now() + 600;
      hexIn.value = h;
      hexIn.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function handleHostMessage(raw) {
      var msg = raw;
      if (typeof msg === 'string') {
        try { msg = JSON.parse(msg); } catch (e) { return; }
      }
      if (!msg || typeof msg !== 'object') return;

      if (msg.type === 'sai2:state') {
        if (typeof msg.attached === 'boolean') attached = msg.attached;
        var bridge = msg.bridge;
        setStatus(bridge === 'ok' || bridge === 'error' || bridge === 'unsupported' ? bridge : 'unsupported');
        renderAttach();
      } else if (msg.type === 'sai2:color') {
        setColorFromHost(msg.hex);
      } else if (msg.type === 'apply:ack') {
        if (msg.ok === true) flashAck();
      }
    }
    window.__cwFromHost = handleHostMessage;

    /* 宿主若用 PostWebMessageAsJson 送达，也能收到（两种通道都认） */
    if (webview && typeof webview.addEventListener === 'function') {
      webview.addEventListener('message', function (e) { handleHostMessage(e.data); });
    }

    var ackTimer = 0;
    function flashAck() {
      applyBtn.classList.add('cw-ack');
      clearTimeout(ackTimer);
      ackTimer = setTimeout(function () { applyBtn.classList.remove('cw-ack'); }, 120);
    }

    /* ---- 4. 页面导出：拦下载链接，内容交给宿主保存 ----
       原页面用 <a download> + Blob 下载（CSS / JSON / GPL / ASE / 保存图片），
       WebView2 里下载策略由环境决定；这里统一拦截，把字节发给宿主写文件，
       任何嵌入式环境都能导出，且能弹原生保存对话框。 */
    document.addEventListener('click', function (e) {
      var t = e.target;
      if (!t || t.nodeType !== 1) return;
      var a = t.closest ? t.closest('a[download]') : null;
      if (!a) return;
      e.preventDefault();
      e.stopPropagation();
      var name = a.getAttribute('download') || 'colorwitch-export';
      var href = a.href;
      fetch(href).then(function (r) { return r.blob(); }).then(function (blob) {
        var fr = new FileReader();
        fr.onload = function () {
          var s = String(fr.result || '');
          var comma = s.indexOf(',');
          post({ type: 'app:save', name: name, mime: blob.type || '', data: comma >= 0 ? s.slice(comma + 1) : s });
        };
        fr.readAsDataURL(blob);
      }).catch(function (err) {
        console.warn('[ColorWitch] 导出失败', err);
      });
    }, true);

    /* ---- 4. 取色 → 宿主：拖拽 30Hz 节流 live:true，松手补 live:false ---- */
    var dragActive = false;
    var dragDirty = false;
    var liveLast = 0;
    var liveTimer = 0;
    var discTimer = 0;
    var lastSent = '';

    /* 同一个色值不重复发给宿主；live:false 是收尾确认，照发 */
    function sendApply(hex, live) {
      if (live && hex === lastSent) return;
      lastSent = hex;
      post({ type: 'app:apply', hex: hex, live: live });
    }

    function pushLive(hex) {
      var now = performance.now();
      var wait = 34 - (now - liveLast);
      if (wait <= 0) {
        if (liveTimer) { clearTimeout(liveTimer); liveTimer = 0; }
        liveLast = now;
        sendApply(hex, true);
      } else if (!liveTimer) {
        liveTimer = setTimeout(function () {
          liveTimer = 0;
          if (!dragActive) return;
          liveLast = performance.now();
          var h = currentHex();
          if (h) sendApply(h, true);
        }, wait);
      }
    }

    function pushDiscrete() {
      clearTimeout(discTimer);
      discTimer = setTimeout(function () {
        if (!realtime) return;
        var h = currentHex();
        if (h) sendApply(h, false);
      }, 160);
    }

    function onColorChanged(hex) {
      if (!realtime) return;
      if (dragActive) { dragDirty = true; pushLive(hex); }
      else pushDiscrete();
    }

    var mo = new MutationObserver(function () {
      var hex = currentHex();
      if (!hex || hex === lastHex) return;
      lastHex = hex;
      if (echo.hex === hex && Date.now() < echo.until) return; /* 宿主推来的色，不回写 */
      onColorChanged(hex);
    });
    mo.observe(cmdHex, { childList: true, characterData: true, subtree: true });

    /* 拖拽会话：三维视口里的任何按键拖拽，或拖动三条滑条 */
    function isDragSource(t) {
      if (!t || t.nodeType !== 1) return false;
      if (t.id === 'gl') return true;
      return t.tagName === 'INPUT' && t.type === 'range';
    }
    document.addEventListener('pointerdown', function (e) {
      if (isDragSource(e.target)) { dragActive = true; dragDirty = false; }
    }, true);
    function endDrag() {
      if (!dragActive) return;
      dragActive = false;
      if (liveTimer) { clearTimeout(liveTimer); liveTimer = 0; }
      if (realtime && dragDirty) {
        dragDirty = false;
        var h = currentHex();
        if (h) sendApply(h, false);
      }
    }
    window.addEventListener('pointerup', endDrag);
    window.addEventListener('pointercancel', endDrag);
    window.addEventListener('blur', endDrag);

    /* ---- 5. 操作条事件 ---- */
    applyBtn.addEventListener('click', function () {
      var hex = currentHex();
      if (hex) sendApply(hex, false);
    });
    readBtn.addEventListener('click', function () { post({ type: 'app:pick' }); });
    attachBtn.addEventListener('click', function () { post({ type: 'app:toggleAttach' }); });
    liveBox.addEventListener('change', function () {
      realtime = liveBox.checked;
      if (!realtime) return;
      var hex = currentHex();
      if (hex) sendApply(hex, false); /* 打开即同步一次 */
    });

    setStatus('unsupported');
    renderAttach();

    /* ---- 6. 浏览器调试用的宿主 mock ---- */
    if (!hasWebView) {
      var mock = {
        color: '#D9663D',
        attached: true,
        sent: [],
        postMessage: function (msg) {
          this.sent.push(msg);
          console.log('[ColorWitch → 宿主 mock]', msg.type, msg);
          var self = this;
          if (msg.type === 'app:apply') {
            self.color = msg.hex;
            setTimeout(function () {
              handleHostMessage({ type: 'apply:ack', ok: true, hex: msg.hex });
            }, 16);
          } else if (msg.type === 'app:pick') {
            setTimeout(function () {
              handleHostMessage({ type: 'sai2:color', hex: self.color });
            }, 16);
          } else if (msg.type === 'app:toggleAttach') {
            self.attached = !self.attached;
            setTimeout(function () {
              handleHostMessage({ type: 'sai2:state', attached: self.attached, visible: true, bridge: 'ok' });
            }, 16);
          }
        }
      };
      window.__cwHostMock = mock;
      handleHostMessage({ type: 'sai2:state', attached: true, visible: true, bridge: 'ok' });
      handleHostMessage({ type: 'sai2:color', hex: mock.color });
    }

    /* ---- 7. 让页面按新尺寸重排 + 上报宽度 ---- */
    var resizeTimer = 0;
    function relayout() {
      measureHeader();
      window.dispatchEvent(new Event('resize'));
    }
    relayout();
    requestAnimationFrame(relayout);
    setTimeout(relayout, 80);

    function reportWidth() {
      var w = Math.round(document.documentElement.clientWidth || window.innerWidth || 0);
      if (w) post({ type: 'app:resize', width: w });
    }
    reportWidth();
    window.addEventListener('resize', function () {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () {
        relayout();
        reportWidth();
      }, 120);
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { markRoot(); boot(); });
  } else {
    boot();
  }
})();
