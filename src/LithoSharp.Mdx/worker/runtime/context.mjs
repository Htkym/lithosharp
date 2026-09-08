import {createContext, useEffect} from 'react';
export const PageContext = createContext({basePath: '/', url: '/', locale: 'en'});
export const hydrationStarts = new Map();
export function HydrationProbe({id, children}) {
  useEffect(() => {
    const start = hydrationStarts.get(id);
    if (start !== undefined) {
      hydrationStarts.delete(id);
      document.dispatchEvent(new CustomEvent('lithosharp:hydrated', {detail: {id, milliseconds: performance.now() - start}}));
    }
  }, [id]);
  return children;
}
