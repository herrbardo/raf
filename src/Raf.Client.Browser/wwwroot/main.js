import { dotnet } from './_framework/dotnet.js';

// Todo lo que RAF guarda en el navegador lleva este prefijo.
const prefix = 'raf:';

function area(name) {
  return name === 'session' ? sessionStorage : localStorage;
}

/**
 * "Duplicar pestaña" copia el sessionStorage, con la sesión adentro. Si las dos pestañas renovaran el mismo
 * refresh token, el servidor lo tomaría como robo y cerraría todas las sesiones. Cada pestaña tiene un id; si al
 * arrancar otra pestaña viva responde que ese id es suyo, esta es una copia y descarta lo copiado.
 */
async function guardDuplicatedTab() {
  const key = prefix + 'tab';
  const channel = new BroadcastChannel('raf-tabs');
  let id = sessionStorage.getItem(key);
  if (id) {
    const taken = await new Promise(resolve => {
      const timer = setTimeout(() => resolve(false), 250);
      channel.onmessage = e => {
        if (e.data?.type === 'mine' && e.data.id === id) {
          clearTimeout(timer);
          resolve(true);
        }
      };
      channel.postMessage({ type: 'who', id });
    });
    if (taken) {
      for (const k of Object.keys(sessionStorage)) if (k.startsWith(prefix)) sessionStorage.removeItem(k);
      id = null;
    }
  }
  if (!id) {
    id = crypto.randomUUID();
    sessionStorage.setItem(key, id);
  }
  channel.onmessage = e => {
    if (e.data?.type === 'who' && e.data.id === id) channel.postMessage({ type: 'mine', id });
  };
}

await guardDuplicatedTab();

const runtime = await dotnet
  .withConfig({ loadAllSatelliteResources: true }) // los textos en inglés
  .withApplicationArgumentsFromQuery()
  .create();

runtime.setModuleImports('raf', {
  storage: {
    get: (name, key) => area(name).getItem(prefix + key),
    set: (name, key, value) => {
      try {
        area(name).setItem(prefix + key, value);
      } catch (e) {
        console.warn('RAF: no se pudo guardar en el navegador', e);
      }
    },
    remove: (name, key) => area(name).removeItem(prefix + key),
  },
  page: {
    reload: () => location.reload(),
  },
});

const config = runtime.getConfig();
const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
window.addEventListener('beforeunload', e => {
  if (exports.Raf.Client.Browser.BrowserInterop.ShouldWarnOnLeave()) {
    e.preventDefault();
    e.returnValue = '';
  }
});

await runtime.runMain(config.mainAssemblyName, []);
document.getElementById('loading')?.remove();
