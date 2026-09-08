import React, {useEffect, useId, useRef, useState} from 'react';
import runtime from 'lithosharp:live-runtime';

// User code runs only in an opaque sandbox origin, with no storage, network or parent DOM access.
// JavaScript React examples use React.createElement and render(); JSX needs a separate compiler.
export default function LiveCode({code, fallback = 'Run the example to see its output.', title = 'React playground'}) {
  const [source, setSource] = useState(code);
  const [documentSource, setDocumentSource] = useState(null);
  const [error, setError] = useState('');
  const frame = useRef(null);
  const id = useId();
  useEffect(() => {
    const receive = event => { if (event.source === frame.current?.contentWindow && event.data?.kind === 'lithosharp-live-error') setError(String(event.data.message).slice(0, 2000)); };
    window.addEventListener('message', receive);
    return () => window.removeEventListener('message', receive);
  }, []);
  function run() {
    setError('');
    const report = `const report=error=>parent.postMessage({kind:'lithosharp-live-error',message:String(error)},'*');addEventListener('error',event=>report(event.message));addEventListener('unhandledrejection',event=>report(event.reason));`;
    const execute = `try{new Function(${JSON.stringify(source)})();}catch(error){report(error);}`;
    const script = (report + runtime + execute).replace(/<\/script/gi, '<\\/script');
    setDocumentSource(`<html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'"></head><body><div id="root"></div><script>${script}</script></body></html>`);
  }
  return React.createElement('section', {'aria-label': title},
    React.createElement('label', {htmlFor: id}, title + ' (JavaScript; React.createElement and render)'),
    React.createElement('textarea', {id, value: source, onChange: event => setSource(event.target.value), rows: 10, spellCheck: false}),
    React.createElement('button', {type: 'button', onClick: run}, 'Run'),
    React.createElement('pre', null, fallback),
    documentSource ? React.createElement('iframe', {ref: frame, title, sandbox: 'allow-scripts', referrerPolicy: 'no-referrer', srcDoc: documentSource}) : null,
    React.createElement('p', {role: 'alert'}, error));
}
