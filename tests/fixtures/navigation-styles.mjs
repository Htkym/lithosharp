import test from 'node:test';
import assert from 'node:assert/strict';
import {prepareNavigationStyles} from '../../src/LithoSharp.Mdx/Browser/client.mjs';
class Link extends EventTarget {
  constructor(href, media = null) { super(); this.attrs = new Map([['href',href]]); if(media !== null) this.attrs.set('media',media); this.parent = null; this.removed = 0; }
  getAttribute(k) { return this.attrs.get(k) ?? null; }
  setAttribute(k,v) { this.attrs.set(k,v); }
  removeAttribute(k) { this.attrs.delete(k); }
  remove() { this.removed++; if(this.parent) this.parent.links.splice(this.parent.links.indexOf(this),1); this.parent=null; }
}
function doc(links) {
 const head={links, querySelectorAll(){return [...this.links];},append(link){ if(link.parent) link.parent.links.splice(link.parent.links.indexOf(link),1); this.links.push(link); link.parent=this; }};
 links.forEach(link=>link.parent=head);
 return {head,baseURI:'https://example.test/docs/one/',importNode(source){const copy=new Link('');copy.attrs=new Map(source.attrs);return copy;}};
}
test('shared styles retain identity and are never removed',async()=>{
 const a=new Link('/site.css'),b=new Link('/theme.css');const live=doc([a,b]);
 const tx=await prepareNavigationStyles(live,doc([new Link('/site.css'),new Link('/theme.css')]),new URL('https://example.test/docs/two/'),new AbortController().signal);
 tx.commit();assert.deepEqual(live.head.links,[a,b]);assert.equal(a.removed+b.removed,0);
});
test('old styles stay active until new styles load; commit preserves cascade',async()=>{
 const a=new Link('/site.css'),old=new Link('/old.css');const live=doc([a,old]);
 const pending=prepareNavigationStyles(live,doc([new Link('/site.css'),new Link('../new.css')]),new URL('https://example.test/docs/two/'),new AbortController().signal);
 const added=live.head.links[2]; assert.equal(added.getAttribute('media'),'not all');assert.equal(old.removed,0);
 assert.equal(added.getAttribute('href'),'https://example.test/docs/new.css');
 added.dispatchEvent(new Event('load')); const tx=await pending; assert.equal(old.removed,0);
 tx.commit();assert.deepEqual(live.head.links,[a,added]);assert.equal(added.getAttribute('media'),null);assert.equal(old.removed,1);
});
test('abort removes staged sheets without disturbing current page',async()=>{
 const old=new Link('/site.css');const live=doc([old]),controller=new AbortController();
 const pending=prepareNavigationStyles(live,doc([new Link('/new.css')]),new URL('https://example.test/docs/'),controller.signal);
 controller.abort(); await assert.rejects(pending);assert.deepEqual(live.head.links,[old]);assert.equal(old.removed,0);
});
test('CSS load failure leaves current sheets intact',async()=>{
 const old=new Link('/site.css');const live=doc([old]);
 const pending=prepareNavigationStyles(live,doc([new Link('/new.css')]),new URL('https://example.test/docs/'),new AbortController().signal);
 live.head.links[1].dispatchEvent(new Event('error')); await assert.rejects(pending);assert.deepEqual(live.head.links,[old]);
});
test('changed cascade order and media are respected',async()=>{
 const a=new Link('/a.css'),b=new Link('/b.css');const live=doc([a,b]);
 const tx=await prepareNavigationStyles(live,doc([new Link('/b.css'),new Link('/a.css')]),new URL('https://example.test/docs/'),new AbortController().signal);
 tx.commit();assert.deepEqual(live.head.links,[b,a]);assert.equal(a.removed+b.removed,0);
});
test('shared relative stylesheet keeps its resolved URL across navigation depths',async()=>{
 const shared=new Link('../theme.css');const live=doc([shared]);
 const destination=new URL('https://example.test/docs/two/deep/');
 const tx=await prepareNavigationStyles(live,doc([new Link('../../theme.css')]),destination,new AbortController().signal);
 tx.commit();live.baseURI=destination.href;
 assert.deepEqual(live.head.links,[shared]);assert.equal(shared.removed,0);
 assert.equal(new URL(shared.getAttribute('href'),live.baseURI).href,'https://example.test/docs/theme.css');
});
