define([], function () {
    'use strict';

    var pluginId = '6c1f7a52-3d0e-4b8a-9e51-2f6d4c8b7a19';

    // Make sure the emby-* custom elements are registered (no-op if already loaded).
    try {
        if (window.Emby && Emby.importModule) {
            [
                './modules/emby-elements/emby-input/emby-input.js',
                './modules/emby-elements/emby-select/emby-select.js',
                './modules/emby-elements/emby-checkbox/emby-checkbox.js',
                './modules/emby-elements/emby-button/emby-button.js',
                './modules/emby-elements/emby-button/paper-icon-button-light.js'
            ].forEach(function (m) { Emby.importModule(m).catch(function () { }); });
        }
    } catch (e) { /* older web client: elements are already global */ }

    function showLoading() {
        try { if (window.Dashboard && Dashboard.showLoadingMsg) { Dashboard.showLoadingMsg(); } } catch (e) { }
    }

    function hideLoading() {
        try { if (window.Dashboard && Dashboard.hideLoadingMsg) { Dashboard.hideLoadingMsg(); } } catch (e) { }
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function pick(o, name) {
        if (!o) { return undefined; }
        if (o[name] !== undefined) { return o[name]; }
        var camel = name.charAt(0).toLowerCase() + name.slice(1);
        return o[camel];
    }

    function formatWhen(iso) {
        if (!iso) { return 'never'; }
        var d = new Date(iso);
        return isNaN(d.getTime()) ? iso : d.toLocaleString();
    }

    function View(view, params) {
        var self = this;
        self.view = view;

        function api() {
            try {
                if (self.getApiClient) {
                    var c = self.getApiClient();
                    if (c) { return c; }
                }
            } catch (e) { }
            return window.ApiClient;
        }

        function q(sel) { return view.querySelector(sel); }

        function setSelect(sel, value) {
            var el = q(sel);
            el.value = value == null ? '' : String(value);
            if (el.value !== String(value == null ? '' : value) && el.options.length) {
                el.selectedIndex = 0;
            }
        }

        function loadUsers(selectedId) {
            return api().getUsers().then(function (users) {
                var html = '<option value="">Select a user</option>';
                (users || []).forEach(function (u) {
                    html += '<option value="' + esc(u.Id) + '">' + esc(u.Name) + '</option>';
                });
                q('#mvfUser').innerHTML = html;
                q('#mvfUser').value = selectedId || '';
            });
        }

        function renderLastSync(cfg) {
            var status = cfg.LastSyncStatus || 'No sync has run yet.';
            q('.mvfLastSync').innerHTML =
                '<b>Last sync:</b> ' + esc(formatWhen(cfg.LastSyncUtc)) + ' &mdash; ' + esc(status) +
                (cfg.LayoutId ? '<br/><b>Dispatcharr layout id:</b> <code>' + esc(cfg.LayoutId) + '</code>' : '');
        }

        function load() {
            showLoading();
            api().getPluginConfiguration(pluginId).then(function (cfg) {
                self.config = cfg;
                q('#mvfEnabled').checked = !!cfg.Enabled;
                q('#mvfUrl').value = cfg.DispatcharrUrl || '';
                q('#mvfDashPath').value = cfg.DashPath == null ? '/dash' : cfg.DashPath;
                q('#mvfUsername').value = cfg.DispatcharrUsername || '';
                q('#mvfPassword').value = cfg.DispatcharrPassword || '';
                q('#mvfName').value = cfg.MultiviewName || '';
                setSelect('#mvfMax', cfg.MaxStreams || 4);
                setSelect('#mvfOrder', cfg.TileOrder || 'channel');
                self.manualOrder = (cfg.ManualOrder || []).slice();
                self.manualRows = null;
                updateOrderUi();
                setSelect('#mvfLayout', cfg.LayoutStyle == null ? 'auto' : cfg.LayoutStyle);
                setSelect('#mvfAudio', cfg.AudioSource == null ? '0' : cfg.AudioSource);
                q('#mvfRestart').checked = !!cfg.RestartActiveStream;
                q('#mvfGuide').checked = cfg.RefreshEmbyGuideOnCreate !== false;
                renderLastSync(cfg);
                return loadUsers(cfg.EmbyUserId);
            }).then(hideLoading, function (err) {
                hideLoading();
                q('.mvfResult').innerHTML = '<p style="color:#e53935;">Could not load settings: ' + esc(err && err.message ? err.message : err) + '</p>';
            });
        }

        // ---------------------------------------------------------- manual tile order

        function maxStreams() {
            return parseInt(q('#mvfMax').value, 10) || 4;
        }

        function updateOrderUi() {
            var manual = q('#mvfOrder').value === 'manual';
            q('.mvfManualSection').classList.toggle('hide', !manual);
            if (manual && !self.manualRows) { loadManualList(); }
        }

        function currentManualIds() {
            return self.manualRows
                ? self.manualRows.map(function (r) { return pick(r, 'EmbyId'); })
                : (self.manualOrder || []);
        }

        function loadManualList() {
            var list = q('.mvfManualList');
            list.innerHTML = '<div style="padding:.8em 1em;">Loading favorites&hellip;</div>';
            callApi('GET', 'MultiviewFavorites/Preview', null, {
                TileOrder: 'manual',
                ManualOrder: currentManualIds().join(',')
            }).then(function (r) {
                if (!pick(r, 'Success')) {
                    list.innerHTML = '<div style="padding:.8em 1em;color:#e53935;">' + esc(pick(r, 'Message')) + '</div>';
                    return;
                }
                self.manualRows = pick(r, 'Rows') || [];
                renderManualList();
            }, function (err) {
                list.innerHTML = '<div style="padding:.8em 1em;color:#e53935;">' + esc(errorText(err)) + '</div>';
            });
        }

        function renderManualList() {
            var rows = self.manualRows || [];
            var list = q('.mvfManualList');
            if (!rows.length) {
                list.innerHTML = '<div style="padding:.8em 1em;">No favorite Live TV channels found for this user. Save the user first, then Reload favorites.</div>';
                return;
            }
            var max = maxStreams();
            var tile = 0;
            var html = '';
            rows.forEach(function (row, i) {
                var eligible = pick(row, 'Eligible');
                var label;
                if (eligible) {
                    tile++;
                    label = tile <= max ? '<b>Tile ' + tile + '</b>' : 'Over the ' + max + '-stream limit';
                } else {
                    label = esc(pick(row, 'Status'));
                }
                var dim = !eligible || tile > max;
                html += '<div class="mvfManualRow" style="display:flex;align-items:center;gap:.6em;padding:.25em .6em;' +
                    (i ? 'border-top:1px solid rgba(128,128,128,.15);' : '') + (dim ? 'opacity:.55;' : '') + '">' +
                    '<div style="min-width:3.5em;text-align:right;font-variant-numeric:tabular-nums;">' + esc(pick(row, 'Number')) + '</div>' +
                    '<div style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + esc(pick(row, 'EmbyName')) + '</div>' +
                    '<div style="flex:0 1 auto;font-size:90%;text-align:right;">' + label + '</div>' +
                    '<button type="button" is="paper-icon-button-light" class="btnMvfMove" data-index="' + i + '" data-dir="-1" title="Move up"' + (i === 0 ? ' disabled' : '') + '><i class="md-icon">arrow_upward</i></button>' +
                    '<button type="button" is="paper-icon-button-light" class="btnMvfMove" data-index="' + i + '" data-dir="1" title="Move down"' + (i === rows.length - 1 ? ' disabled' : '') + '><i class="md-icon">arrow_downward</i></button>' +
                    '</div>';
            });
            list.innerHTML = html;
        }

        function channelSortKey(row) {
            var n = parseFloat(String(pick(row, 'Number') || '').replace('-', '.'));
            return isNaN(n) ? Number.MAX_VALUE : n;
        }

        view.querySelector('.mvfManualList').addEventListener('click', function (e) {
            var btn = e.target.closest('.btnMvfMove');
            if (!btn || !self.manualRows) { return; }
            var i = parseInt(btn.getAttribute('data-index'), 10);
            var j = i + parseInt(btn.getAttribute('data-dir'), 10);
            if (j < 0 || j >= self.manualRows.length) { return; }
            var tmp = self.manualRows[i];
            self.manualRows[i] = self.manualRows[j];
            self.manualRows[j] = tmp;
            renderManualList();
            var moved = view.querySelector('.btnMvfMove[data-index="' + j + '"][data-dir="' + btn.getAttribute('data-dir') + '"]');
            if (moved && !moved.disabled) { moved.focus(); }
        });

        view.querySelector('.btnMvfResetOrder').addEventListener('click', function () {
            if (!self.manualRows) { return; }
            self.manualRows.sort(function (a, b) {
                var d = channelSortKey(a) - channelSortKey(b);
                return d !== 0 ? d : String(pick(a, 'EmbyName')).localeCompare(String(pick(b, 'EmbyName')));
            });
            renderManualList();
        });

        view.querySelector('.btnMvfReload').addEventListener('click', loadManualList);
        view.querySelector('#mvfOrder').addEventListener('change', updateOrderUi);
        view.querySelector('#mvfMax').addEventListener('change', function () {
            if (self.manualRows) { renderManualList(); }
        });

        function renderResult(r) {
            var ok = pick(r, 'Success');
            var dry = pick(r, 'DryRun');
            var msg = pick(r, 'Message') || '';
            var warnings = pick(r, 'Warnings') || [];
            var rows = pick(r, 'Rows') || [];

            var html = '<p style="font-weight:600;color:' + (ok ? '#43a047' : '#e53935') + ';">' +
                (dry ? 'Preview: ' : '') + esc(msg) + '</p>';

            warnings.forEach(function (w) {
                html += '<p style="color:#fb8c00;margin:.3em 0;">&#9888; ' + esc(w) + '</p>';
            });

            var showFav = rows.some(function (row) { return !!pick(row, 'FavoritedUtc'); });

            if (rows.length) {
                html += '<div style="overflow-x:auto;"><table style="width:100%;border-collapse:collapse;margin-top:.8em;font-size:92%;">' +
                    '<thead><tr style="text-align:left;border-bottom:1px solid rgba(128,128,128,.4);">' +
                    '<th style="padding:.4em .6em;">#</th><th style="padding:.4em .6em;">Emby favorite</th>' +
                    '<th style="padding:.4em .6em;">Dispatcharr channel</th>' +
                    (showFav ? '<th style="padding:.4em .6em;">Favorited</th>' : '') +
                    '<th style="padding:.4em .6em;">Result</th></tr></thead><tbody>';
                rows.forEach(function (row) {
                    var included = pick(row, 'Included');
                    html += '<tr style="border-bottom:1px solid rgba(128,128,128,.15);' + (included ? '' : 'opacity:.6;') + '">' +
                        '<td style="padding:.4em .6em;">' + esc(pick(row, 'Number')) + '</td>' +
                        '<td style="padding:.4em .6em;">' + esc(pick(row, 'EmbyName')) + '</td>' +
                        '<td style="padding:.4em .6em;">' + esc(pick(row, 'DispatcharrName') || '—') + '</td>' +
                        (showFav ? '<td style="padding:.4em .6em;white-space:nowrap;">' + esc(pick(row, 'FavoritedUtc') ? formatWhen(pick(row, 'FavoritedUtc')) : '—') + '</td>' : '') +
                        '<td style="padding:.4em .6em;' + (included ? 'font-weight:600;' : '') + '">' + esc(pick(row, 'Status')) + '</td></tr>';
                });
                html += '</tbody></table></div>';
            } else if (ok) {
                html += '<p>No favorite Live TV channels found for this user.</p>';
            }

            q('.mvfResult').innerHTML = html;
        }

        function callApi(type, path, body, query) {
            var client = api();
            var opts = { type: type, url: client.getUrl(path, query), dataType: 'json' };
            if (body) {
                opts.data = JSON.stringify(body);
                opts.contentType = 'application/json';
            }
            return client.ajax(opts);
        }

        function errorText(err) {
            if (!err) { return 'Request failed.'; }
            if (err.status === 401 || err.status === 403) { return 'Not authorized (admin only).'; }
            return err.message || err.statusText || ('Request failed' + (err.status ? ' (' + err.status + ')' : '') + '.');
        }

        view.querySelector('.mvfForm').addEventListener('submit', function (e) {
            e.preventDefault();
            e.stopPropagation();
            showLoading();

            api().getPluginConfiguration(pluginId).then(function (cfg) {
                cfg.Enabled = q('#mvfEnabled').checked;
                cfg.DispatcharrUrl = q('#mvfUrl').value.trim();
                cfg.DashPath = q('#mvfDashPath').value.trim();
                cfg.DispatcharrUsername = q('#mvfUsername').value.trim();
                cfg.DispatcharrPassword = q('#mvfPassword').value;
                cfg.EmbyUserId = q('#mvfUser').value;
                cfg.MultiviewName = q('#mvfName').value.trim() || 'Emby Favorites';
                cfg.MaxStreams = parseInt(q('#mvfMax').value, 10) || 4;
                cfg.TileOrder = q('#mvfOrder').value || 'channel';
                cfg.ManualOrder = currentManualIds();
                cfg.LayoutStyle = q('#mvfLayout').value;
                cfg.AudioSource = q('#mvfAudio').value;
                cfg.RestartActiveStream = q('#mvfRestart').checked;
                cfg.RefreshEmbyGuideOnCreate = q('#mvfGuide').checked;

                return api().updatePluginConfiguration(pluginId, cfg);
            }).then(function (result) {
                self.manualOrder = currentManualIds();
                if (window.Dashboard && Dashboard.processPluginConfigurationUpdateResult) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                } else {
                    hideLoading();
                }
                if (q('#mvfEnabled').checked) {
                    q('.mvfResult').innerHTML = '<p>Saved. Syncing in the background&hellip; use Preview to see the result.</p>';
                }
            }, function (err) {
                hideLoading();
                q('.mvfResult').innerHTML = '<p style="color:#e53935;">Save failed: ' + esc(errorText(err)) + '</p>';
            });

            return false;
        });

        view.querySelector('.btnMvfTest').addEventListener('click', function () {
            var out = q('.mvfTestResult');
            out.innerHTML = 'Testing&hellip;';
            callApi('POST', 'MultiviewFavorites/Test', {
                DispatcharrUrl: q('#mvfUrl').value.trim(),
                DashPath: q('#mvfDashPath').value.trim(),
                DispatcharrUsername: q('#mvfUsername').value.trim(),
                DispatcharrPassword: q('#mvfPassword').value
            }).then(function (r) {
                var ok = pick(r, 'Success');
                out.innerHTML = '<span style="font-weight:600;color:' + (ok ? '#43a047' : '#e53935') + ';">' +
                    (ok ? '&#10004; ' : '&#10008; ') + esc(pick(r, 'Message')) + '</span>';
            }, function (err) {
                out.innerHTML = '<span style="color:#e53935;">' + esc(errorText(err)) + '</span>';
            });
        });

        function runAction(type, path, busyText, query) {
            q('.mvfResult').innerHTML = '<p>' + busyText + '</p>';
            showLoading();
            callApi(type, path, null, query).then(function (r) {
                hideLoading();
                renderResult(r);
                return api().getPluginConfiguration(pluginId).then(renderLastSync);
            }, function (err) {
                hideLoading();
                q('.mvfResult').innerHTML = '<p style="color:#e53935;">' + esc(errorText(err)) + '</p>';
            });
        }

        view.querySelector('.btnMvfPreview').addEventListener('click', function () {
            runAction('GET', 'MultiviewFavorites/Preview', 'Checking favorites&hellip;', {
                TileOrder: q('#mvfOrder').value,
                ManualOrder: currentManualIds().join(',')
            });
        });

        view.querySelector('.btnMvfSync').addEventListener('click', function () {
            runAction('POST', 'MultiviewFavorites/Sync', 'Syncing&hellip;');
        });

        view.addEventListener('viewshow', load);
    }

    return View;
});
