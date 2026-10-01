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

    function newId() {
        var s = '';
        for (var i = 0; i < 32; i++) { s += Math.floor(Math.random() * 16).toString(16); }
        return s;
    }

    function confirmDialog(text) {
        try {
            if (window.Emby && Emby.importModule) {
                return Emby.importModule('./modules/common/dialogs/confirm.js').then(function (confirm) {
                    var fn = confirm && (confirm.default || confirm);
                    return fn(text);
                });
            }
        } catch (e) { }
        return window.confirm(text) ? Promise.resolve() : Promise.reject();
    }

    var GREEN = '#43a047', RED = '#e53935', ORANGE = '#fb8c00';

    function View(view, params) {
        var self = this;
        self.view = view;
        self.profiles = [];
        self.users = [];
        self.selectedId = null;
        self.manualRows = {};   // profile id -> rows from the server, in the order shown

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

        function callApi(type, path, body) {
            var client = api();
            var opts = { type: type, url: client.getUrl(path), dataType: 'json' };
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

        // ---------------------------------------------------------- profiles

        function selected() {
            for (var i = 0; i < self.profiles.length; i++) {
                if (self.profiles[i].Id === self.selectedId) { return self.profiles[i]; }
            }
            return null;
        }

        function userName(id) {
            for (var i = 0; i < self.users.length; i++) {
                if (self.users[i].Id === id) { return self.users[i].Name; }
            }
            return null;
        }

        function uniqueName(base) {
            var taken = {};
            self.profiles.forEach(function (p) { taken[String(p.MultiviewName || '').trim().toLowerCase()] = true; });
            if (!taken[base.toLowerCase()]) { return base; }
            for (var n = 2; ; n++) {
                var candidate = base + ' ' + n;
                if (!taken[candidate.toLowerCase()]) { return candidate; }
            }
        }

        function newProfile(userId, name) {
            return {
                Id: newId(),
                Enabled: true,
                EmbyUserId: userId || '',
                MultiviewName: uniqueName(name || 'Emby Favorites'),
                MaxStreams: 4,
                TileOrder: 'channel',
                ManualOrder: [],
                LayoutStyle: 'auto',
                AudioSource: '0',
                LayoutId: '',
                LastSyncUtc: '',
                LastSyncStatus: ''
            };
        }

        function manualIds(p) {
            var rows = self.manualRows[p.Id];
            return rows ? rows.map(function (r) { return pick(r, 'EmbyId'); }) : (p.ManualOrder || []);
        }

        function duplicateNames() {
            var seen = {}, dup = {};
            self.profiles.forEach(function (p) {
                var k = String(p.MultiviewName || '').trim().toLowerCase();
                if (seen[k]) { dup[k] = true; }
                seen[k] = true;
            });
            return dup;
        }

        function renderList() {
            var list = q('.mvfProfileList');
            if (!self.profiles.length) {
                list.innerHTML = '<div style="padding:.9em 1em;">No multiview channels yet. Add one, or add one for each user.</div>';
                return;
            }
            var dup = duplicateNames();
            var html = '';
            self.profiles.forEach(function (p, i) {
                var isSel = p.Id === self.selectedId;
                var user = userName(p.EmbyUserId);
                var problem = dup[String(p.MultiviewName || '').trim().toLowerCase()] ? 'Name used twice'
                    : (p.Enabled && !p.EmbyUserId) ? 'No user chosen' : '';
                var status = problem
                    ? '<span style="color:' + RED + ';">' + esc(problem) + '</span>'
                    : !p.Enabled ? '<span style="opacity:.7;">Paused</span>'
                    : p.LastSyncStatus
                        ? '<span style="color:' + (/^Failed/.test(p.LastSyncStatus) ? RED : 'inherit') + ';opacity:.85;">' + esc(p.LastSyncStatus) + '</span>'
                        : '<span style="opacity:.7;">Not synced yet</span>';
                html += '<button type="button" class="mvfProfileRow" data-id="' + esc(p.Id) + '" style="display:flex;width:100%;align-items:center;gap:1em;text-align:left;' +
                    'padding:.7em 1em;border:0;margin:0;cursor:pointer;color:inherit;font:inherit;' +
                    (i ? 'border-top:1px solid rgba(128,128,128,.2);' : '') +
                    'background:' + (isSel ? 'rgba(82,181,75,.18)' : 'transparent') + ';' + (p.Enabled ? '' : 'opacity:.65;') + '">' +
                    '<i class="md-icon" style="opacity:.75;">' + (p.Enabled ? 'grid_view' : 'pause_circle') + '</i>' +
                    '<span style="flex:1;min-width:0;">' +
                    '<span style="display:block;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + esc(p.MultiviewName || '(unnamed)') + '</span>' +
                    '<span style="display:block;font-size:88%;opacity:.8;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' +
                    esc(user || 'No user') + ' &middot; up to ' + esc(p.MaxStreams) + ' tiles' +
                    (p.LayoutId ? ' &middot; layout <code>' + esc(p.LayoutId) + '</code>' : '') + '</span>' +
                    '</span>' +
                    '<span style="flex:0 1 45%;font-size:88%;text-align:right;">' + status + '</span>' +
                    '</button>';
            });
            list.innerHTML = html;
        }

        function fillUserSelect() {
            var html = '<option value="">Select a user</option>';
            self.users.forEach(function (u) {
                html += '<option value="' + esc(u.Id) + '">' + esc(u.Name) + '</option>';
            });
            q('#mvfUser').innerHTML = html;
        }

        function select(id) {
            self.selectedId = id;
            var p = selected();
            q('.mvfEditor').classList.toggle('hide', !p);
            q('.mvfResult').innerHTML = '';
            renderList();
            if (!p) { return; }

            q('.mvfEditorTitle').textContent = p.MultiviewName || '(unnamed)';
            q('#mvfProfileEnabled').checked = p.Enabled !== false;
            setSelect('#mvfUser', p.EmbyUserId || '');
            q('#mvfName').value = p.MultiviewName || '';
            setSelect('#mvfMax', p.MaxStreams || 4);
            setSelect('#mvfOrder', p.TileOrder || 'channel');
            setSelect('#mvfLayout', p.LayoutStyle == null ? 'auto' : p.LayoutStyle);
            setSelect('#mvfAudio', p.AudioSource == null ? '0' : p.AudioSource);
            q('.mvfProfileStatus').innerHTML = p.LastSyncUtc
                ? '<b>Last sync:</b> ' + esc(formatWhen(p.LastSyncUtc)) + ' &mdash; ' + esc(p.LastSyncStatus || '')
                : '';
            updateOrderUi();
        }

        function bindField(sel, evt, apply) {
            q(sel).addEventListener(evt, function () {
                var p = selected();
                if (!p) { return; }
                apply(p, q(sel));
                renderList();
            });
        }

        bindField('#mvfProfileEnabled', 'change', function (p, el) { p.Enabled = el.checked; });
        bindField('#mvfUser', 'change', function (p, el) {
            p.EmbyUserId = el.value;
            delete self.manualRows[p.Id];   // different user, different favorites
            updateOrderUi();
        });
        bindField('#mvfName', 'input', function (p, el) {
            p.MultiviewName = el.value;
            q('.mvfEditorTitle').textContent = el.value.trim() || '(unnamed)';
        });
        bindField('#mvfMax', 'change', function (p, el) {
            p.MaxStreams = parseInt(el.value, 10) || 4;
            if (self.manualRows[p.Id]) { renderManualList(); }
        });
        bindField('#mvfOrder', 'change', function (p, el) { p.TileOrder = el.value; updateOrderUi(); });
        bindField('#mvfLayout', 'change', function (p, el) { p.LayoutStyle = el.value; });
        bindField('#mvfAudio', 'change', function (p, el) { p.AudioSource = el.value; });

        q('.mvfProfileList').addEventListener('click', function (e) {
            var row = e.target.closest('.mvfProfileRow');
            if (row) { select(row.getAttribute('data-id')); }
        });

        q('.btnMvfAdd').addEventListener('click', function () {
            var p = newProfile('', 'Emby Favorites');
            self.profiles.push(p);
            select(p.Id);
            q('#mvfUser').focus();
        });

        q('.btnMvfAddAll').addEventListener('click', function () {
            var added = null;
            self.users.forEach(function (u) {
                var has = self.profiles.some(function (p) { return p.EmbyUserId === u.Id; });
                if (has) { return; }
                var possessive = /s$/i.test(u.Name) ? u.Name + "'" : u.Name + "'s";
                var p = newProfile(u.Id, possessive + ' Favorites');
                self.profiles.push(p);
                if (!added) { added = p; }
            });
            if (added) {
                select(added.Id);
                showFormMessage('Added a multiview for each user without one. Review them, then Save.', GREEN);
            } else {
                showFormMessage('Every user already has a multiview.', 'inherit');
            }
        });

        q('.btnMvfRemove').addEventListener('click', function () {
            var p = selected();
            if (!p) { return; }
            var text = 'Remove "' + (p.MultiviewName || 'this multiview') + '"?' +
                (p.LayoutId
                    ? '\n\nWhen you save, its layout is also deleted from Dispatcharr. To stop syncing it but keep the layout, untick "Sync this multiview" instead.'
                    : '');
            confirmDialog(text).then(function () {
                var idx = self.profiles.indexOf(p);
                self.profiles.splice(idx, 1);
                delete self.manualRows[p.Id];
                var next = self.profiles[Math.min(idx, self.profiles.length - 1)];
                select(next ? next.Id : null);
                showFormMessage('Removed. Save to apply.', 'inherit');
            }, function () { });
        });

        function showFormMessage(text, color) {
            var el = q('.mvfFormError');
            el.style.color = color || RED;
            el.textContent = text || '';
        }

        // ---------------------------------------------------------- manual tile order

        function updateOrderUi() {
            var p = selected();
            var manual = !!p && q('#mvfOrder').value === 'manual';
            q('.mvfManualSection').classList.toggle('hide', !manual);
            if (manual) {
                if (self.manualRows[p.Id]) { renderManualList(); } else { loadManualList(); }
            }
        }

        function previewBody(p, overrides) {
            var body = {
                Id: p.Id,
                EmbyUserId: p.EmbyUserId,
                MultiviewName: p.MultiviewName,
                MaxStreams: p.MaxStreams,
                TileOrder: p.TileOrder,
                ManualOrder: manualIds(p),
                LayoutStyle: p.LayoutStyle,
                AudioSource: p.AudioSource,
                LayoutId: p.LayoutId
            };
            Object.keys(overrides || {}).forEach(function (k) { body[k] = overrides[k]; });
            return body;
        }

        function loadManualList() {
            var p = selected();
            if (!p) { return; }
            var list = q('.mvfManualList');
            if (!p.EmbyUserId) {
                list.innerHTML = '<div style="padding:.8em 1em;">Choose a user first.</div>';
                return;
            }
            var forId = p.Id;
            list.innerHTML = '<div style="padding:.8em 1em;">Loading favorites&hellip;</div>';
            callApi('POST', 'MultiviewFavorites/Preview', previewBody(p, { TileOrder: 'manual' })).then(function (r) {
                var pr = (pick(r, 'Profiles') || [])[0];
                if (!pr || !pick(pr, 'Success')) {
                    if (self.selectedId === forId) {
                        list.innerHTML = '<div style="padding:.8em 1em;color:' + RED + ';">' + esc(pick(pr, 'Message') || pick(r, 'Message')) + '</div>';
                    }
                    return;
                }
                self.manualRows[forId] = pick(pr, 'Rows') || [];
                if (self.selectedId === forId) { renderManualList(); }
            }, function (err) {
                list.innerHTML = '<div style="padding:.8em 1em;color:' + RED + ';">' + esc(errorText(err)) + '</div>';
            });
        }

        function renderManualList() {
            var p = selected();
            var rows = (p && self.manualRows[p.Id]) || [];
            var list = q('.mvfManualList');
            if (!rows.length) {
                list.innerHTML = '<div style="padding:.8em 1em;">No favorite Live TV channels found for this user.</div>';
                return;
            }
            var max = p.MaxStreams || 4;
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

        q('.mvfManualList').addEventListener('click', function (e) {
            var btn = e.target.closest('.btnMvfMove');
            var p = selected();
            var rows = p && self.manualRows[p.Id];
            if (!btn || !rows) { return; }
            var i = parseInt(btn.getAttribute('data-index'), 10);
            var j = i + parseInt(btn.getAttribute('data-dir'), 10);
            if (j < 0 || j >= rows.length) { return; }
            var tmp = rows[i];
            rows[i] = rows[j];
            rows[j] = tmp;
            p.ManualOrder = manualIds(p);
            renderManualList();
            var moved = view.querySelector('.btnMvfMove[data-index="' + j + '"][data-dir="' + btn.getAttribute('data-dir') + '"]');
            if (moved && !moved.disabled) { moved.focus(); }
        });

        q('.btnMvfResetOrder').addEventListener('click', function () {
            var p = selected();
            var rows = p && self.manualRows[p.Id];
            if (!rows) { return; }
            rows.sort(function (a, b) {
                var d = channelSortKey(a) - channelSortKey(b);
                return d !== 0 ? d : String(pick(a, 'EmbyName')).localeCompare(String(pick(b, 'EmbyName')));
            });
            p.ManualOrder = manualIds(p);
            renderManualList();
        });

        q('.btnMvfReload').addEventListener('click', loadManualList);

        // ---------------------------------------------------------- preview / sync

        function rowsTable(rows) {
            var showFav = rows.some(function (row) { return !!pick(row, 'FavoritedUtc'); });
            var html = '<div style="overflow-x:auto;"><table style="width:100%;border-collapse:collapse;margin-top:.6em;font-size:92%;">' +
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
            return html + '</tbody></table></div>';
        }

        function profileResultHtml(pr, heading) {
            var ok = pick(pr, 'Success');
            var rows = pick(pr, 'Rows') || [];
            var html = heading
                ? '<h4 style="margin:1.2em 0 .2em;">' + esc(pick(pr, 'Name')) + (pick(pr, 'UserName') ? ' <span style="font-weight:normal;opacity:.75;">(' + esc(pick(pr, 'UserName')) + ')</span>' : '') + '</h4>'
                : '';
            html += '<p style="font-weight:600;margin:.3em 0;color:' + (ok ? GREEN : RED) + ';">' + esc(pick(pr, 'Message')) + '</p>';
            (pick(pr, 'Warnings') || []).forEach(function (w) {
                html += '<p style="color:' + ORANGE + ';margin:.3em 0;">&#9888; ' + esc(w) + '</p>';
            });
            if (rows.length) { html += rowsTable(rows); }
            else if (ok) { html += '<p style="margin:.3em 0;">No favorite Live TV channels found for this user.</p>'; }
            return html;
        }

        q('.btnMvfPreview').addEventListener('click', function () {
            var p = selected();
            if (!p) { return; }
            var out = q('.mvfResult');
            out.innerHTML = '<p>Checking favorites&hellip;</p>';
            callApi('POST', 'MultiviewFavorites/Preview', previewBody(p)).then(function (r) {
                var pr = (pick(r, 'Profiles') || [])[0];
                out.innerHTML = pr
                    ? '<div style="opacity:.75;font-size:90%;">Preview</div>' + profileResultHtml(pr, false)
                    : '<p style="color:' + RED + ';">' + esc(pick(r, 'Message')) + '</p>';
            }, function (err) {
                out.innerHTML = '<p style="color:' + RED + ';">' + esc(errorText(err)) + '</p>';
            });
        });

        q('.btnMvfSync').addEventListener('click', function () {
            var out = q('.mvfSyncResult');
            out.innerHTML = '<p>Syncing&hellip;</p>';
            showLoading();
            callApi('POST', 'MultiviewFavorites/Sync').then(function (r) {
                hideLoading();
                var html = '<p style="font-weight:600;color:' + (pick(r, 'Success') ? GREEN : RED) + ';">' + esc(pick(r, 'Message')) + '</p>';
                (pick(r, 'Profiles') || []).forEach(function (pr) { html += profileResultHtml(pr, true); });
                out.innerHTML = html;
                refreshStatuses();
            }, function (err) {
                hideLoading();
                out.innerHTML = '<p style="color:' + RED + ';">' + esc(errorText(err)) + '</p>';
            });
        });

        // ---------------------------------------------------------- load / save

        function renderLastSync(cfg) {
            q('.mvfLastSync').innerHTML = '<b>Last sync:</b> ' + esc(formatWhen(cfg.LastSyncUtc)) +
                (cfg.LastSyncStatus ? ' &mdash; ' + esc(cfg.LastSyncStatus) : '');
        }

        // Pull fresh sync statuses/layout ids without touching unsaved edits.
        function refreshStatuses() {
            return api().getPluginConfiguration(pluginId).then(function (cfg) {
                (cfg.Profiles || []).forEach(function (saved) {
                    self.profiles.forEach(function (p) {
                        if (p.Id !== saved.Id) { return; }
                        p.LayoutId = saved.LayoutId;
                        p.LastSyncUtc = saved.LastSyncUtc;
                        p.LastSyncStatus = saved.LastSyncStatus;
                    });
                });
                renderLastSync(cfg);
                renderList();
                var p = selected();
                if (p && p.LastSyncUtc) {
                    q('.mvfProfileStatus').innerHTML = '<b>Last sync:</b> ' + esc(formatWhen(p.LastSyncUtc)) + ' &mdash; ' + esc(p.LastSyncStatus || '');
                }
            });
        }

        function load() {
            showLoading();
            Promise.all([api().getPluginConfiguration(pluginId), api().getUsers()]).then(function (res) {
                var cfg = res[0];
                self.users = (res[1] || []).map(function (u) { return { Id: u.Id, Name: u.Name }; });
                fillUserSelect();

                q('#mvfEnabled').checked = !!cfg.Enabled;
                q('#mvfUrl').value = cfg.DispatcharrUrl || '';
                q('#mvfDashPath').value = cfg.DashPath == null ? '/dash' : cfg.DashPath;
                q('#mvfUsername').value = cfg.DispatcharrUsername || '';
                q('#mvfPassword').value = cfg.DispatcharrPassword || '';
                q('#mvfRestart').checked = !!cfg.RestartActiveStream;
                q('#mvfGuide').checked = cfg.RefreshEmbyGuideOnCreate !== false;

                self.profiles = (cfg.Profiles || []).map(function (p) { return Object.assign({}, p); });
                self.manualRows = {};
                var keep = self.profiles.some(function (p) { return p.Id === self.selectedId; });
                select(keep ? self.selectedId : (self.profiles[0] ? self.profiles[0].Id : null));
                renderLastSync(cfg);
                showFormMessage('');
                hideLoading();
            }, function (err) {
                hideLoading();
                showFormMessage('Could not load settings: ' + (err && err.message ? err.message : err));
            });
        }

        function validate() {
            var dup = duplicateNames();
            for (var i = 0; i < self.profiles.length; i++) {
                var p = self.profiles[i];
                var label = '"' + (String(p.MultiviewName || '').trim() || '(unnamed)') + '"';
                if (!String(p.MultiviewName || '').trim()) { select(p.Id); return 'Give every multiview a name.'; }
                if (dup[String(p.MultiviewName).trim().toLowerCase()]) { select(p.Id); return 'Two multiviews are named ' + label + '. Each needs its own name.'; }
                if (p.Enabled && !p.EmbyUserId) { select(p.Id); return 'Choose an Emby user for ' + label + ' (or untick "Sync this multiview").'; }
            }
            return null;
        }

        q('.mvfForm').addEventListener('submit', function (e) {
            e.preventDefault();
            e.stopPropagation();

            var problem = validate();
            if (problem) {
                showFormMessage(problem);
                return false;
            }
            showFormMessage('');
            showLoading();

            api().getPluginConfiguration(pluginId).then(function (cfg) {
                cfg.Enabled = q('#mvfEnabled').checked;
                cfg.DispatcharrUrl = q('#mvfUrl').value.trim();
                cfg.DashPath = q('#mvfDashPath').value.trim();
                cfg.DispatcharrUsername = q('#mvfUsername').value.trim();
                cfg.DispatcharrPassword = q('#mvfPassword').value;
                cfg.RestartActiveStream = q('#mvfRestart').checked;
                cfg.RefreshEmbyGuideOnCreate = q('#mvfGuide').checked;
                cfg.Profiles = self.profiles.map(function (p) {
                    var copy = Object.assign({}, p);
                    copy.MultiviewName = String(p.MultiviewName || '').trim();
                    copy.ManualOrder = manualIds(p);
                    return copy;
                });
                return api().updatePluginConfiguration(pluginId, cfg);
            }).then(function (result) {
                if (window.Dashboard && Dashboard.processPluginConfigurationUpdateResult) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                } else {
                    hideLoading();
                }
                if (q('#mvfEnabled').checked) {
                    showFormMessage('Saved. Syncing in the background…', GREEN);
                    setTimeout(function () {
                        refreshStatuses().then(function () { showFormMessage(''); }, function () { });
                    }, 5000);
                }
            }, function (err) {
                hideLoading();
                showFormMessage('Save failed: ' + errorText(err));
            });

            return false;
        });

        q('.btnMvfTest').addEventListener('click', function () {
            var out = q('.mvfTestResult');
            out.innerHTML = 'Testing&hellip;';
            callApi('POST', 'MultiviewFavorites/Test', {
                DispatcharrUrl: q('#mvfUrl').value.trim(),
                DashPath: q('#mvfDashPath').value.trim(),
                DispatcharrUsername: q('#mvfUsername').value.trim(),
                DispatcharrPassword: q('#mvfPassword').value
            }).then(function (r) {
                var ok = pick(r, 'Success');
                out.innerHTML = '<span style="font-weight:600;color:' + (ok ? GREEN : RED) + ';">' +
                    (ok ? '&#10004; ' : '&#10008; ') + esc(pick(r, 'Message')) + '</span>';
            }, function (err) {
                out.innerHTML = '<span style="color:' + RED + ';">' + esc(errorText(err)) + '</span>';
            });
        });

        view.addEventListener('viewshow', load);
    }

    return View;
});
