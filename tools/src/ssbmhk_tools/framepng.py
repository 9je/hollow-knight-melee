"""A frame dumped by core/tools/frame_dump.c (Melee's drawing decoded by the core: triangles, vertex colours, textures),
rendered to a PNG the way the mod draws it (fighters, items and effects only; runs back to front; texture x colour), so a
drawing bug (a black turnip, G&W's hatching) can be looked at without the game. usage: ssbmhk-frame-png <frame.bin>
<out.png> [span [centre_x centre_y]] [--alpha] [--size N] (Melee units; the view fits the drawn vertices by default;
--alpha: a transparent background instead of the grey, for art made from a frame)."""
import sys, struct, numpy as np
from PIL import Image
def load(path):
    b=open(path,'rb').read(); o=0
    nv,nr,fx,fy=struct.unpack_from('<iiff',b,o); o+=16
    V=np.frombuffer(b,dtype=np.dtype([('x','<f4'),('y','<f4'),('z','<f4'),('r','u1'),('g','u1'),('b','u1'),('a','u1'),('u','<f4'),('v','<f4')]),count=nv,offset=o); o+=nv*24
    R=np.frombuffer(b,dtype=np.dtype([('first','<u4'),('count','<u4'),('tex','<i4'),('blend','u1'),('plink','u1'),('ws','u1'),('wt','u1'),('gobj','<u4')]),count=nr,offset=o); o+=nr*20
    ns=struct.unpack_from('<i',b,o)[0]; o+=4
    T={}
    for k in range(ns):
        tid,w,h=struct.unpack_from('<iII',b,o); o+=12
        if w and h:
            T[tid]=np.frombuffer(b,dtype=np.uint8,count=w*h*4,offset=o).reshape(h,w,4).astype(np.float32)/255; o+=w*h*4
    return V,R,T,fx,fy
def sample(tex,u,v,ws,wt):
    h,w,_=tex.shape
    def wrap(c,mode):
        if mode==1: return c-np.floor(c)
        if mode==2: t=c-2*np.floor(c/2); return np.where(t>1,2-t,t)
        return np.clip(c,0,1)
    u=wrap(u,ws); v=wrap(v,wt)
    xi=np.clip((u*(w-1)).astype(int),0,w-1); yi=np.clip((v*(h-1)).astype(int),0,h-1)
    return tex[yi,xi]
def render(path,out,size=600,span=None,center=None,alpha=False):
    V,R,T,fx,fy=load(path)
    R=R[np.isin(R['plink'],[8,9,11,12])]   # what the mod draws: fighters, items, effects
    keep=np.zeros(len(V),bool)
    for r in R: keep[r['first']:r['first']+r['count']]=True
    # the view: around the drawn vertices (the 2nd..98th percentiles: the stage's far corners don't count)
    xs=np.percentile(V['x'][keep],[0,100]); ys=np.percentile(V['y'][keep],[0,100])
    cx,cy=center if center else ((xs[0]+xs[1])/2,(ys[0]+ys[1])/2)
    sp=span or max(xs[1]-xs[0],ys[1]-ys[0])*0.6+1
    print('fighter at',fx,fy,'view centre',cx,cy,'span',sp)
    img=np.full((size,size,3),0.35,np.float32)   # a grey backdrop: black parts show
    cov=np.zeros((size,size,1),np.float32)         # how much of each pixel was drawn (--alpha)
    def tosc(x,y): return (x-cx)/sp*size/2+size/2, size/2-(y-cy)/sp*size/2
    for run in R:   # already back to front
        f,c=int(run['first']),int(run['count']); tex=T.get(int(run['tex']))
        for t in range(f,f+c-2,3):
            p=V[t:t+3]
            sx,sy=tosc(p['x'],p['y'])
            x0,x1=int(max(0,np.floor(sx.min()))),int(min(size-1,np.ceil(sx.max())))
            y0,y1=int(max(0,np.floor(sy.min()))),int(min(size-1,np.ceil(sy.max())))
            if x1<x0 or y1<y0: continue
            xs,ys=np.meshgrid(np.arange(x0,x1+1),np.arange(y0,y1+1))
            d=(sx[1]-sx[0])*(sy[2]-sy[0])-(sx[2]-sx[0])*(sy[1]-sy[0])
            if abs(d)<1e-9: continue
            l1=((sx[1]-xs)*(sy[2]-ys)-(sx[2]-xs)*(sy[1]-ys))/d
            l2=((sx[2]-xs)*(sy[0]-ys)-(sx[0]-xs)*(sy[2]-ys))/d
            l3=1-l1-l2
            m=(l1>=-1e-4)&(l2>=-1e-4)&(l3>=-1e-4)
            if not m.any(): continue
            col=np.stack([l1*p['r'][0]+l2*p['r'][1]+l3*p['r'][2], l1*p['g'][0]+l2*p['g'][1]+l3*p['g'][2], l1*p['b'][0]+l2*p['b'][1]+l3*p['b'][2]],-1)/255
            al=(l1*p['a'][0]+l2*p['a'][1]+l3*p['a'][2])/255
            if tex is not None:
                u=l1*p['u'][0]+l2*p['u'][1]+l3*p['u'][2]; v=l1*p['v'][0]+l2*p['v'][1]+l3*p['v'][2]
                tx=sample(tex,u,v,int(run['ws']),int(run['wt']))
                col=col*tx[...,:3]; al=al*tx[...,3]
            al=np.clip(al,0,1)[...,None]*m[...,None]
            blend=int(run['blend'])
            dst=img[y0:y1+1,x0:x1+1]; cv=cov[y0:y1+1,x0:x1+1]
            if blend==3: dst*=1-al+col*al   # a decal pass (the mod's Particles/Multiply): the frame so far times the texture
            elif blend==2: dst+=col*al; cv[:]=np.maximum(cv,al)
            elif blend==1: dst[:]=dst*(1-al)+col*al; cv[:]=cv+al*(1-cv)
            else: dst[:]=np.where(m[...,None],col,dst); cv[:]=np.where(m[...,None],1.0,cv)
    if alpha:
        rgba=np.concatenate([np.clip(img,0,1),np.clip(cov,0,1)],-1)
        Image.fromarray((rgba*255).astype(np.uint8),'RGBA').save(out)
    else: Image.fromarray((np.clip(img,0,1)*255).astype(np.uint8)).save(out)
    print(out, len(V), len(R), len(T), 'textures', {k:v.shape[:2] for k,v in list(T.items())[:12]})
def main(argv=None):
    a=list(sys.argv[1:] if argv is None else argv)
    alpha='--alpha' in a
    if alpha: a.remove('--alpha')
    size=600
    if '--size' in a: i=a.index('--size'); size=int(a[i+1]); del a[i:i+2]
    render(a[0],a[1],size=size,span=float(a[2]) if len(a)>2 else None, center=(float(a[3]),float(a[4])) if len(a)>4 else None,alpha=alpha)
    return 0

if __name__=='__main__':
    sys.exit(main())
