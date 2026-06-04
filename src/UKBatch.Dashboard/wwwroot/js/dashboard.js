// UKBatch.Dashboard browser helpers.
// Loaded after blazor.web.js — module-free (no import) so the inline theme-bootstrap
// script in App.razor can run BEFORE this file loads without race.

(function () {
    'use strict';

    const THEME_KEY = 'ukbatch-dashboard-theme';

    window.UKBatchDashboard = {
        /** Switch theme + persist. Called by Settings.razor via JSRuntime.InvokeVoidAsync. */
        setTheme: function (theme) {
            const value = theme === 'light' ? 'light' : 'dark';
            try { localStorage.setItem(THEME_KEY, value); } catch (e) { /* private mode / quota */ }
            const html = document.documentElement;
            html.classList.remove('dark', 'light');
            html.classList.add(value);
        },
        /** Read current theme without forcing a JS interop call. */
        getTheme: function () {
            try { return localStorage.getItem(THEME_KEY) || 'dark'; }
            catch (e) { return 'dark'; }
        },
        /** Copy a string to clipboard; returns true on success. */
        copyToClipboard: async function (text) {
            try { await navigator.clipboard.writeText(text); return true; }
            catch (e) { return false; }
        },
    };
})();
