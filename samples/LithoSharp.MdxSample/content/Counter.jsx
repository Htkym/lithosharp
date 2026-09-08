import {useId, useState} from 'react';
import styles from './counter.module.css';
export default function Counter({initial = 0}) {
  const [count, setCount] = useState(initial);
  return <button id={useId()} className={styles.counter} onClick={() => setCount(count + 1)}>Count {count}</button>;
}
