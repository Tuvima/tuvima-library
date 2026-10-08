// Dashboard UI kit — Home view (composed).
// No left dock — top bar is the only nav surface. Hero + topbar are full-bleed.
function HomeView({ onOpen, onNavTab, activeTab }) {
  const D = window.TUVIMA_DATA;
  const tabs = [
    { key:'home',    label:'Home' },
    { key:'recent',  label:'Recently Added' },
    { key:'collections', label:'Collections', count: 12 },
    { key:'action',  label:'Review Queue', count: 3, amber: true },
  ];

  return (
    <>
      <div className="ambient"></div>
      <div className="content-layer">
        <TopBar tabs={tabs} activeTab={activeTab} onTab={onNavTab} showBrand />
        <HeroCarousel slides={D.hero} onOpen={onOpen} />
        <div className="shell-inner">
          <ActionStrip onOpen={() => onNavTab('action')} />
          <Lane title="Continue Your Journey"  sub="Pick up where you left off" items={D.continue}  onOpen={onOpen} />
          <Lane title="Recently Added"         sub="New in your media library this week" items={D.recent}    onOpen={onOpen} />
          <Lane title="You Might Like"         sub="Based on your library"     items={D.might_like} onOpen={onOpen} />
        </div>
      </div>
    </>
  );
}

// App shell — routes home / collection detail.
function App() {
  const [route, setRoute] = React.useState(() => {
    try { return JSON.parse(localStorage.getItem('tuvima-route')||'null') || { view:'home', tab:'home' }; }
    catch { return { view:'home', tab:'home' }; }
  });
  React.useEffect(() => { localStorage.setItem('tuvima-route', JSON.stringify(route)); }, [route]);

  const go = (v, extra={}) => setRoute({ view:v, ...extra });

  return (
    <>
      {route.view === 'home' && (
        <HomeView
          onOpen={(id)=>go('detail',{slug:id})}
          onNavTab={(t)=>setRoute(r=>({...r, tab:t}))}
          activeTab={route.tab||'home'}
        />
      )}
      {route.view === 'detail' && (
        <CollectionDetail slug={route.slug} onBack={()=>go('home',{tab:'home'})} />
      )}
    </>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);

