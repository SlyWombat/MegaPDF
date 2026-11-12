// The landing pages' one script (#845): puts the visitor's own platform's store
// first and rings its badge, and says so in the page's language (the sentence is
// the #foryou element's data-text, with {os} for the platform). It runs entirely
// in the browser: it reads the user agent, sends nothing and stores nothing.
(function () {
    var ua = navigator.userAgent, os = null, name = '';
    if (/iPhone|iPod/.test(ua)) { os = 'ios'; name = 'iPhone'; }
    else if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) { os = 'ios'; name = 'iPad'; }
    else if (/Android/.test(ua)) { os = 'android'; name = 'Android'; }
    else if (/Windows/.test(ua)) { os = 'windows'; name = 'Windows'; }
    else if (/Macintosh/.test(ua)) { os = 'mac'; name = 'Mac'; }
    else if (/Linux|X11/.test(ua) && !/CrOS/.test(ua)) { os = 'linux'; name = 'Linux'; }
    var stores = document.getElementById('stores');
    var mine = os && stores && stores.querySelector('[data-os="' + os + '"]');
    if (!mine) return;
    mine.classList.add('mine');
    stores.insertBefore(mine, stores.firstChild);
    var p = document.getElementById('foryou');
    var parts = p.getAttribute('data-text').split('{os}');
    p.textContent = '';
    p.appendChild(document.createTextNode(parts[0]));
    var b = document.createElement('b');
    b.textContent = name;
    p.appendChild(b);
    p.appendChild(document.createTextNode(parts[1] || ''));
    p.hidden = false;
})();
