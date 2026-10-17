// Dashboard UI kit — reusable components.
// All share a single base data source (window.TUVIMA_DATA). JSX.

const ICON = 'assets/icons/';  // relative to ui_kits/dashboard/index.html
const IMG  = 'assets/images/';

/* ── Icon ─────────────────────────────────────────────────────────────── */
function Icon({ name, size = 16, className = '', style = {} }) {
  return (
    <img
      src={`../../assets/icons/${name}.svg`}
      alt=""
      className={className}
      style={{
        width: size, height: size,
        filter: 'invert(1) brightness(.95)',
        opacity: 0.72,
        ...style,
      }}
    />
  );
}

/* ── Dock (left floating nav) ─────────────────────────────────────────── */
function Dock({ active = 'home', onNav }) {
  const items = [
    { key:'home',        icon:'house' },
    { key:'read',        icon:'book-open-reader' },
    { key:'watch',       icon:'film' },
    { key:'listen',      icon:'headphones' },
    { key:'collections', icon:'boxes-stacked' },
    { key:'media library',       icon:'folder-tree' },
  ];
  const footer = [
    { key:'intel',    icon:'wand-magic-sparkles' },
    { key:'engine',   icon:'microchip' },
    { key:'settings', icon:'gear' },
  ];
  return (
    <div className="dock">
      <div className="brand"><img src="../../assets/images/tuvima-icon.svg" alt="Tuvima"/></div>
      {items.map(i => (
        <button key={i.key} className={`d-item ${active===i.key?'active':''}`} onClick={() => onNav && onNav(i.key)}>
          <Icon name={i.icon} />
        </button>
      ))}
      <div className="dock-sep"></div>
      {footer.map(i => (
        <button key={i.key} className={`d-item ${active===i.key?'active':''}`} onClick={() => onNav && onNav(i.key)}>
          <Icon name={i.icon} />
        </button>
      ))}
    </div>
  );
}

/* ── Top bar ──────────────────────────────────────────────────────────── */
function TopBar({ tabs, activeTab, onTab, onSearch, showBrand }) {
  return (
    <div className="topbar">
      {showBrand && (
        <div className="brand">
          <img src="../../assets/images/tuvima-logo-on-dark.svg" alt="Tuvima"/>
        </div>
      )}
      <div className="tabs">
        {tabs.map(t => (
          <button key={t.key} className={`tab ${activeTab===t.key?'active':''}`} onClick={() => onTab && onTab(t.key)}>
            {t.label}
            {t.count != null && <span className={`num ${t.amber?'amber':''}`}>{t.count}</span>}
          </button>
        ))}
      </div>
      <div className="tab-sp"></div>
      <div className="topbar-actions">
        <button className="act" onClick={onSearch}><Icon name="magnifying-glass" size={14} /></button>
        <button className="act"><Icon name="sliders" size={14} /></button>
        <button className="act"><Icon name="gear" size={14} /></button>
        <div className="avatar">JM</div>
      </div>
    </div>
  );
}

/* ── Hero carousel ────────────────────────────────────────────────────── */
function HeroCarousel({ slides, onOpen }) {
  const [idx, setIdx] = React.useState(0);
  const n = slides.length;

  React.useEffect(() => {
    const id = setInterval(() => setIdx(i => (i+1) % n), 6000);
    return () => clearInterval(id);
  }, [n]);

  // Update ambient color
  React.useEffect(() => {
    document.documentElement.style.setProperty('--ambient', slides[idx].dominant);
  }, [idx, slides]);

  const prev = () => setIdx(i => (i-1+n) % n);
  const next = () => setIdx(i => (i+1) % n);

  return (
    <div className="hero">
      {slides.map((s, i) => (
        <div key={s.id} className={`hero-slide ${i===idx?'active':''}`} style={{ background: s.bg }}>
          <div className="hero-body">
            <div className="hero-eyebrow">{s.eyebrow}</div>
            <h1 className="hero-title">{s.title}</h1>
            <p className="hero-sub">{s.sub}</p>
            <div className="hero-ctas">
              <button className="cta" onClick={() => onOpen && onOpen(s.id)}>
                <span className="play"></span>{s.cta}
                {s.progress > 0 && <div className="cta-progress" style={{ width: s.progress + '%' }}></div>}
              </button>
              <button className="cta-ghost" onClick={() => onOpen && onOpen(s.id)}>View Collection</button>
            </div>
          </div>
        </div>
      ))}
      <div className="hero-ctrls">
        <button className="hero-arrow" onClick={prev} aria-label="prev">‹</button>
        {slides.map((_, i) => (
          <button key={i} className={`hero-dot ${i===idx?'active':''}`} onClick={() => setIdx(i)} aria-label={`slide ${i+1}`}></button>
        ))}
        <button className="hero-arrow" onClick={next} aria-label="next">›</button>
      </div>
    </div>
  );
}

/* ── Section header ───────────────────────────────────────────────────── */
function SectionHead({ title, sub, seeAll }) {
  return (
    <div className="section-head">
      <h2>{title}</h2>
      {sub && <span className="sub">{sub}</span>}
      {seeAll && <button className="see-all">See all →</button>}
    </div>
  );
}

/* ── Poster ───────────────────────────────────────────────────────────── */
function Poster({ item, onOpen }) {
  return (
    <div className="poster" onClick={() => onOpen && onOpen(item.id)}>
      <div className="art">
        <div className="cover" style={{ background: item.bg }}>
          {item.coverText.split('\n').map((l,i)=><span key={i} style={{ display:'block' }}>{l}</span>)}
        </div>
        {item.ribbon && <div className="ribbon">{item.ribbon}</div>}
        {item.progress != null && (
          <>
            <div className="progress-shade"></div>
            <div className="progress-bar" style={{ width: item.progress + '%' }}></div>
          </>
        )}
      </div>
      <div className="title">{item.title}</div>
      <div className="meta">{item.meta}</div>
      <div className="types">
        {item.types && item.types.map(t => <span key={t} className={`tdot ${t}`}></span>)}
      </div>
    </div>
  );
}

/* ── Swimlane ─────────────────────────────────────────────────────────── */
function Lane({ title, sub, items, onOpen }) {
  return (
    <div className="lane">
      <SectionHead title={title} sub={sub} seeAll />
      <div className="lane-scroll">
        {items.map(it => <Poster key={it.id+it.title} item={it} onOpen={onOpen} />)}
      </div>
    </div>
  );
}

/* ── Action-center strip ──────────────────────────────────────────────── */
function ActionStrip({ onOpen }) {
  return (
    <div className="action-strip">
      <div className="ic"><Icon name="clipboard-check" size={14} /></div>
      <div className="action-body">
        <div className="action-title">3 collections need your review</div>
        <div className="action-desc">The Engine couldn’t decide whether “Dune (2021)” and “Dune (1984)” belong to the same work. Help it learn.</div>
      </div>
      <button className="action-btn" onClick={onOpen}>Resolve Reviews</button>
    </div>
  );
}

/* ── Collection detail ────────────────────────────────────────────────── */
function CollectionDetail({ slug, onBack }) {
  const c = window.TUVIMA_DATA.collection[slug] || window.TUVIMA_DATA.collection.dune;

  React.useEffect(() => {
    document.documentElement.style.setProperty('--ambient', c.dominant);
  }, [c]);

  return (
    <>
      <div className="hero-fixed blurred">
        <div style={{ width:'100%', height:'100%', background: c.coverBg }}></div>
      </div>
      <div className="hero-fade"></div>

      <div className="content-layer">
        <div className="shell-inner detail">
          <button className="back-btn" onClick={onBack}>
            <Icon name="chevron-left" size={10} /> Back to Home
          </button>

          <div className="detail-header">
            <div className="detail-poster">
              <div className="cover" style={{
                width:'100%', height:'100%',
                background: c.coverBg,
                display:'flex', alignItems:'flex-end', padding:'18px',
                color:'#fff', fontFamily:'Merriweather, serif', fontWeight:700,
                fontSize:36, letterSpacing:'.01em', lineHeight:1.05,
              }}>{c.coverText}</div>
            </div>
            <div className="detail-info">
              <div className="detail-eyebrow">{c.eyebrow}</div>
              <h1 className="detail-title">{c.title}</h1>
              <div className="detail-byline">
                {c.byline.map((b,i)=>(
                  <React.Fragment key={i}>
                    {i>0 && <span>·</span>}{b}
                  </React.Fragment>
                ))}
              </div>
              <p style={{ fontSize:14, lineHeight:1.5, color:'rgba(255,255,255,.72)', margin:0, maxWidth:560 }}>{c.description}</p>
              <div className="detail-actions">
                <button className="cta"><span className="play"></span>Continue Reading
                  <div className="cta-progress" style={{ width:'38%' }}></div>
                </button>
                <button className="cta-ghost">Mark as Read</button>
                <button className="cta-ghost">Share</button>
              </div>
            </div>
          </div>

          <div className="detail-tabs">
            <button className="detail-tab active">Overview</button>
            <button className="detail-tab">Details</button>
          </div>

          <section className="detail-overview">
            <h2>Overview</h2>
            <p>{c.description}</p>
          </section>
        </div>
      </div>
    </>
  );
}

function typeColor(t) {
  return {
    'Book':'#A78BFA', 'Audiobook':'#60A5FA', 'Movie':'#F472B6',
    'TV':'#34D399', 'Music':'#FBBF24', 'Comic':'#FB923C',
  }[t] || 'rgba(248,248,248,.5)';
}

// Expose to other scripts
Object.assign(window, { Dock, TopBar, HeroCarousel, SectionHead, Poster, Lane, ActionStrip, CollectionDetail, Icon });

