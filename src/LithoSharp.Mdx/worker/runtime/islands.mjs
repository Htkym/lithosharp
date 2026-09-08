import {createElement, useContext, useId} from 'react';
import {PageContext} from './context.mjs';

function publicProps(value, schema, depth = 0) {
  if (depth > 32 || !schema || typeof schema !== 'object') throw new Error('Island props require a bounded explicit JSON schema.');
  if (Object.keys(schema).some(key => !['type', 'properties', 'items', 'required', 'additionalProperties', 'enum', 'description'].includes(key))) throw new Error('Unsupported Island schema keyword.');
  const type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
  if (schema.type !== type && !(schema.type === 'integer' && Number.isSafeInteger(value))) throw new Error('Island prop type does not match its public schema.');
  if (type === 'number' && !Number.isFinite(value)) throw new Error('Island numbers must be finite.');
  if (schema.enum && !schema.enum.some(choice => JSON.stringify(choice) === JSON.stringify(value))) throw new Error('Island enum value is not declared.');
  if (type === 'object') {
    if (schema.additionalProperties !== false || !schema.properties || Object.getPrototypeOf(value) !== Object.prototype) throw new Error('Island objects must explicitly prohibit additional properties.');
    for (const key of Object.keys(value)) {
      if (['__proto__', 'prototype', 'constructor'].includes(key) || !Object.hasOwn(schema.properties, key)) throw new Error(`Undeclared Island prop '${key}'.`);
      publicProps(value[key], schema.properties[key], depth + 1);
    }
    for (const key of schema.required ?? []) if (!Object.hasOwn(value, key)) throw new Error(`Missing Island prop '${key}'.`);
  }
  if (type === 'array') for (const item of value) publicProps(item, schema.items, depth + 1);
}

export function Island({component, props = {}, schema, strategy = 'load', media, module, exportName, children}) {
  const context = useContext(PageContext);
  const id = 'island-' + useId().replace(/[^a-z0-9-]/gi, '');
  if (!['load', 'idle', 'visible', 'media', 'manual'].includes(strategy)) throw new Error('Unknown Island hydration strategy.');
  if (strategy === 'media' && !media) throw new Error('The media strategy requires a media query.');
  if (!schema) schema = {type: 'object', properties: {}, additionalProperties: false};
  publicProps(props, schema);
  const data = JSON.stringify(props);
  if (new TextEncoder().encode(data).length > 65536) throw new Error('Island props exceed 64 KiB; share data explicitly.');
  if (typeof component !== 'function') throw new Error('Island requires an imported component.');
  if (!context.renderIsland) return createElement('div', {id, 'data-island': strategy}, createElement(component, props, children));
  if (children !== undefined) throw new Error('Put children inside the Island component; only JSON props cross the boundary.');
  const html = context.renderIsland(component, props, id);
  context.islands.push({id, module, exportName, strategy, media, propsBytes: new TextEncoder().encode(data).length});
  return createElement('div', {id, 'data-island': strategy, 'data-island-props': data, dangerouslySetInnerHTML: {__html: html}});
}
