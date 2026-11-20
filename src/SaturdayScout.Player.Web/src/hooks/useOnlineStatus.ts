import { useEffect, useState } from 'react';

export function useOnlineStatus(): boolean {
  const [online, setOnline] = useState(() => typeof navigator === 'undefined' || navigator.onLine !== false);
  useEffect(() => {
    const onlineAgain = () => setOnline(true);
    const offline = () => setOnline(false);
    window.addEventListener('online', onlineAgain);
    window.addEventListener('offline', offline);
    return () => {
      window.removeEventListener('online', onlineAgain);
      window.removeEventListener('offline', offline);
    };
  }, []);
  return online;
}
