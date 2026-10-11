// Shared helpers for the dashboard pages. Plain browser JS, no dependencies.
// Loaded from _Layout <head> so page scripts can call these while they run.
(function (w) {
    'use strict';

    var DEFAULT_ERROR = 'Something went wrong. Please try again.';

    function esc(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function token() {
        try { return localStorage.getItem('aura_token'); } catch (e) { return null; }
    }

    // Readable text for a failed response. Server bodies are {error, message} or ProblemDetails;
    // 5xx and unknown bodies never reach the page as raw text or stack traces.
    function messageFor(status, body) {
        if (status === 0) return 'Could not reach the server. Check your connection and try again.';
        if (status === 403) return 'You do not have permission to do that.';
        if (status === 404) return 'That item no longer exists. Refresh the page and try again.';
        if (status >= 500) return 'The server hit an error. Try again in a moment.';
        if (body && typeof body.message === 'string' && body.message) return body.message;
        if (body && body.errors && typeof body.errors === 'object') {
            var first = [].concat.apply([], Object.keys(body.errors).map(function (k) { return body.errors[k]; }))[0];
            if (first) return String(first);
        }
        if (body && typeof body.title === 'string' && body.title) return body.title;
        return DEFAULT_ERROR;
    }

    // Wraps fetch for the dashboard. Resolves to:
    //   null                      after a 401 (the browser is sent to the login page)
    //   parsed body               on success ({} for 204 / empty bodies)
    //   {error, status, message}  on any failure, so callers only check `data.error`
    async function auraApi(path, opts) {
        opts = opts || {};
        var init = Object.assign({}, opts, {
            headers: Object.assign({ 'Authorization': 'Bearer ' + token(), 'Content-Type': 'application/json' }, opts.headers || {})
        });
        var res;
        try {
            res = await fetch(path, init);
        } catch (e) {
            return { error: 'network', status: 0, message: messageFor(0) };
        }
        if (res.status === 401) { w.location.href = '/dashboard/login'; return null; }
        if (res.status === 204) return {};

        var body = null;
        if ((res.headers.get('content-type') || '').indexOf('application/json') !== -1) {
            try { body = await res.json(); } catch (e) { body = null; }
        }
        if (res.ok) return body == null ? {} : body;

        return {
            error: res.status === 403 ? 'forbidden' : 'request_failed',
            status: res.status,
            message: messageFor(res.status, body)
        };
    }

    // Toast with an optional link, e.g. { href: '/dashboard/run?...', label: 'Open run' }.
    // Toasts with a link stay up longer so there is time to click them.
    function auraToast(msg, type, link) {
        document.querySelectorAll('.toast').forEach(function (t) { t.remove(); });  // one toast at a time; they share a fixed spot
        var el = document.createElement('div');
        el.className = 'toast toast-' + (type || 'success');
        var text = document.createElement('span');
        text.textContent = msg;
        el.appendChild(text);
        if (link) {
            var a = document.createElement('a');
            a.href = link.href;
            a.textContent = link.label;
            el.appendChild(a);
        }
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, link ? 8000 : 3000);
        return el;
    }

    // Friendly empty state: one line on what the page is for, plus the primary action.
    // actionHtml is trusted markup written by the page (buttons/links), never user data.
    function emptyState(text, actionHtml) {
        return '<div class="empty-state"><p>' + esc(text) + '</p>' + (actionHtml || '') + '</div>';
    }

    function errorState(message) {
        return '<div class="alert alert-error">' + esc(message || DEFAULT_ERROR) + '</div>';
    }

    var PROVIDERS = { azure: 'Azure', aws: 'AWS', gcp: 'GCP' };
    function providerLabel(p) {
        if (!p) return '-';
        return PROVIDERS[String(p).toLowerCase()] || String(p);
    }

    var EXECUTORS = {
        operation: 'Cloud SDK', emissionload: 'Container', powershell: 'PowerShell',
        python: 'Python', csharpsdk: 'C# SDK'
    };
    function executorLabel(t) {
        if (!t) return '-';
        return EXECUTORS[String(t).toLowerCase()] || humanize(t);
    }

    // "DeploymentRun" -> "Deployment run", "accept-invite" -> "Accept invite"
    function humanize(s) {
        if (s == null || s === '') return '-';
        var t = String(s).replace(/[-_]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase().trim();
        return t.charAt(0).toUpperCase() + t.slice(1);
    }

    w.esc = esc;
    w.auraApi = auraApi;
    w.auraToast = auraToast;
    w.emptyState = emptyState;
    w.errorState = errorState;
    w.providerLabel = providerLabel;
    w.executorLabel = executorLabel;
    w.humanize = humanize;
})(window);
