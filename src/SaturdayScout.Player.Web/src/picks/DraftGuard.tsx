import { createContext, useContext, useState, type PropsWithChildren } from 'react';

interface DraftGuardValue {
  dirty: boolean;
  setDirty: (dirty: boolean) => void;
}

const DraftGuardContext = createContext<DraftGuardValue | null>(null);

export function DraftGuardProvider({ children }: PropsWithChildren) {
  const [dirty, setDirty] = useState(false);
  return <DraftGuardContext.Provider value={{ dirty, setDirty }}>{children}</DraftGuardContext.Provider>;
}

export function useDraftGuard(): DraftGuardValue {
  const context = useContext(DraftGuardContext);
  if (!context) throw new Error('useDraftGuard must be used inside DraftGuardProvider.');
  return context;
}
